﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Raven.Abstractions.Logging;
using Raven.Client.Data.Indexes;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.MapReduce;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Indexes.Workers
{
    public class MapDocuments : IIndexingWork
    {
        protected readonly ILog Log = LogManager.GetLogger(typeof(CleanupDeletedDocuments));

        private readonly Index _index;
        private readonly IndexingConfiguration _configuration;
        private readonly MapReduceIndexingContext _mapReduceContext;
        private readonly DocumentsStorage _documentsStorage;
        private readonly IndexStorage _indexStorage;

        public MapDocuments(Index index, DocumentsStorage documentsStorage, IndexStorage indexStorage, 
                            IndexingConfiguration configuration, MapReduceIndexingContext mapReduceContext)
        {
            _index = index;
            _configuration = configuration;
            _mapReduceContext = mapReduceContext;
            _documentsStorage = documentsStorage;
            _indexStorage = indexStorage;
        }

        public string Name => "Map";

        public class StatefulEnumerator : IEnumerable<Document>
        {
            private readonly IEnumerable<Document> _docs;
            public Document Current;

            public StatefulEnumerator(IEnumerable<Document> docs)
            {
                _docs = docs;
            }

            public IEnumerator<Document> GetEnumerator()
            {
                foreach (var document in _docs)
                {
                    Current = document;
                    yield return document;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public bool Execute(DocumentsOperationContext databaseContext, TransactionOperationContext indexContext,
            Lazy<IndexWriteOperation> writeOperation, IndexingStatsScope stats, CancellationToken token)
        {
            var pageSize = _configuration.MaxNumberOfDocumentsToFetchForMap;
            var timeoutProcessing = Debugger.IsAttached == false ? _configuration.DocumentProcessingTimeout.AsTimeSpan : TimeSpan.FromMinutes(15);

            var moreWorkFound = false;

            foreach (var collection in _index.Collections)
            {
                using (var collectionStats = stats.For("Collection_" + collection))
                {
                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing map for '{_index.Name} ({_index.IndexId})'. Collection: {collection}.");

                    var lastMappedEtag = _indexStorage.ReadLastIndexedEtag(indexContext.Transaction, collection);

                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing map for '{_index.Name} ({_index.IndexId})'. LastMappedEtag: {lastMappedEtag}.");

                    var lastEtag = lastMappedEtag;
                    var count = 0;

                    var sw = Stopwatch.StartNew();
                    IndexWriteOperation indexWriter = null;

                    using (databaseContext.OpenReadTransaction())
                    {
                        var documents = _documentsStorage.GetDocumentsAfter(databaseContext, collection, lastEtag + 1, 0, pageSize);
                        var stateful = new StatefulEnumerator(documents);
                        foreach (var document in _index.EnumerateMap(stateful, collection, indexContext))
                        {
                            //TODO: take into account time here, if we are on slow i/o system, we don't want to wait for 128K docs before
                            //TODO: we flush the index
                            token.ThrowIfCancellationRequested();

                            if (indexWriter == null)
                                indexWriter = writeOperation.Value;

                            if (Log.IsDebugEnabled)
                                Log.Debug($"Executing map for '{_index.Name} ({_index.IndexId})'. Processing document: {document.Key}.");

                            collectionStats.RecordMapAttempt();
                            var current = stateful.Current;
                            count++;
                            lastEtag = document.Etag;

                            try
                            {
                                _index.HandleMap(document, indexWriter, indexContext, collectionStats);

                                collectionStats.RecordMapSuccess();
                            }
                            catch (Exception e)
                            {
                                collectionStats.RecordMapError();
                                if (Log.IsWarnEnabled)
                                    Log.WarnException($"Failed to execute mapping function on '{document.Key}' for '{_index.Name} ({_index.IndexId})'.", e);

                                collectionStats.AddMapError(document.Key, $"Failed to execute mapping function on {document.Key}. Message: {e.Message}");
                            }

                            if (sw.Elapsed > timeoutProcessing)
                                break;
                        }
                    }

                    if (count == 0)
                        continue;

                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing map for '{_index.Name} ({_index.IndexId})'. Processed {count} documents in '{collection}' collection in {sw.ElapsedMilliseconds:#,#;;0} ms.");

                    if (_index.Type.IsMap())
                    {
                        _indexStorage.WriteLastIndexedEtag(indexContext.Transaction, collection, lastEtag);
                    }
                    else
                    {
                        _mapReduceContext.ProcessedDocEtags[collection] = lastEtag;
                    }

                    moreWorkFound = true;
                }
            }

            return moreWorkFound;
        }
    }
}