﻿using System.Collections.Generic;
using System.Linq;
using Raven.Client.Server;
using Raven.Server.Rachis;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.ServerWide.Commands
{
    public class ModifyDatabaseWatchersCommand : UpdateDatabaseCommand
    {
        public BlittableJsonReaderArray Watchers;

        public ModifyDatabaseWatchersCommand() : base(null)
        {

        }

        public ModifyDatabaseWatchersCommand(string databaseName) : base(databaseName)
        {

        }

        public override string UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            if (Watchers != null)
            {
                record.Topology.Watchers = new List<DatabaseWatcher>(
                    Watchers.Items.Select(
                        i => JsonDeserializationRachis<DatabaseWatcher>.Deserialize((BlittableJsonReaderObject)i)
                    ));
            }
            else
            {
                record.Topology.Watchers = new List<DatabaseWatcher>();
            }

            return null;
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(Watchers)] = Watchers;
        }
    }
}
