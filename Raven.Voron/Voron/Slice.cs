﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Voron.Impl;
using Voron.Trees;

namespace Voron
{
	public unsafe delegate int SliceComparer(byte* a, byte* b, int size);

	public unsafe class Slice
	{
		public static Slice AfterAllKeys = new Slice(SliceOptions.AfterAllKeys);
		public static Slice BeforeAllKeys = new Slice(SliceOptions.BeforeAllKeys);
		public static Slice Empty = new Slice(new byte[0]);

		private ushort _size;
		private readonly byte[] _array;
		private byte* _pointer;

		public SliceOptions Options;

		public ushort Size
		{
			get { return _size; }
		}

		public Slice(SliceOptions options)
		{
			Options = options;
			_pointer = null;
			_array = null;
			_size = 0;
		}

		public Slice(byte* key, ushort size)
		{
			_size = size;
			Options = SliceOptions.Key;
			_array = null;
			_pointer = key;
		}

		public Slice(byte[] key) : this(key, (ushort)key.Length)
		{
			
		}

		public Slice(Slice other, ushort size)
		{
			if (other._array != null)
				_array = other._array;
			else
				_pointer = other._pointer;

			Options = other.Options;
			_size = size;
		}

		public Slice(byte[] key, ushort size)
		{
			if (key == null) throw new ArgumentNullException("key");
			_size = size;
			Options = SliceOptions.Key;
			_pointer = null;
			_array = key;
		}

		public bool Equals(Slice other)
		{
			return Compare(other) == 0;
		}

		public override bool Equals(object obj)
		{
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != GetType()) return false;
			return Equals((Slice)obj);
		}

		public override int GetHashCode()
		{
			if (_array != null)
				return ComputeHashArray();
			return ComputeHashPointer();
		}

		private int ComputeHashPointer()
		{
			unchecked
			{
				const int p = 16777619;
				int hash = (int)2166136261;

				for (int i = 0; i < _size; i++)
					hash = (hash ^ _pointer[i]) * p;

				hash += hash << 13;
				hash ^= hash >> 7;
				hash += hash << 3;
				hash ^= hash >> 17;
				hash += hash << 5;
				return hash;
			}
		}

		private int ComputeHashArray()
		{
			unchecked
			{
				const int p = 16777619;
				int hash = (int)2166136261;

				for (int i = 0; i < _size; i++)
					hash = (hash ^ _array[i]) * p;

				hash += hash << 13;
				hash ^= hash >> 7;
				hash += hash << 3;
				hash ^= hash >> 17;
				hash += hash << 5;
				return hash;
			}
		}

		public override string ToString()
		{
			// this is used for debug purposes only
			if (Options != SliceOptions.Key)
				return Options.ToString();

			if (_array != null)
				return Encoding.UTF8.GetString(_array,0, _size);

			return new string((sbyte*)_pointer, 0, _size, Encoding.UTF8);
		}

		public int Compare(Slice other)
		{
			Debug.Assert(Options == SliceOptions.Key);
			Debug.Assert(other.Options == SliceOptions.Key);

			var r = CompareData(other, NativeMethods.memcmp, Math.Min(Size, other.Size));
			if (r != 0)
				return r;
			return Size - other.Size;
		}

		public bool StartsWith(Slice other)
		{
			if (Size < other.Size)
				return false;
			return CompareData(other, NativeMethods.memcmp, other.Size) == 0;
		}

		private int CompareData(Slice other, SliceComparer cmp, ushort size)
		{
			if (_array != null)
			{
				fixed (byte* a = _array)
				{
					if (other._array != null)
					{
						fixed (byte* b = other._array)
						{
							return cmp(a, b, size);
						}
					}
                    return cmp(a, other._pointer, size);
				}
			}
			if (other._array != null)
			{
				fixed (byte* b = other._array)
				{
                    return cmp(_pointer, b, size);
				}
			}
            return cmp(_pointer, other._pointer, size);
		}

		private class SlicePrefixMatcher
		{
			private readonly int _maxPrefixLength;

			public SlicePrefixMatcher(int maxPrefixLength)
			{
				_maxPrefixLength = maxPrefixLength;
			}

			public int MatchedBytes { get; private set; }

			public int MatchPrefix(byte* a, byte* b, int size)
			{
				MatchedBytes = 0;

				for (var i = 0; i < _maxPrefixLength; i++)
				{
					if (*a == *b)
						MatchedBytes++;
					else
						break;

					a++;
					b++;
				}

				return 0;
			} 
		}

		public int FindPrefixSize(Slice other)
		{
			var slicePrefixMatcher = new SlicePrefixMatcher(Math.Min(Size, other.Size));

			CompareData(other, slicePrefixMatcher.MatchPrefix, 0);

			return slicePrefixMatcher.MatchedBytes;
		}

		public static implicit operator Slice(string s)
		{
			return new Slice(Encoding.UTF8.GetBytes(s));
		}

		public void CopyTo(byte* dest)
		{
			if (_array == null)
			{
				NativeMethods.memcpy(dest, _pointer, _size);
				return;
			}
			fixed (byte* a = _array)
			{
				NativeMethods.memcpy(dest, a, _size);
			}
		}

		public void CopyTo(byte[] dest)
		{
			if (_array == null)
			{
				fixed (byte* p = dest)
					NativeMethods.memcpy(p, _pointer, _size);
				return;
			}
			Buffer.BlockCopy(_array, 0, dest, 0, _size);
		}

		public void CopyTo(int from, byte[] dest, int offset, int count)
		{
			if (from + count > Size)
				throw new ArgumentOutOfRangeException("from", "Cannot copy data after the end of the slice");
			if(offset + count > dest.Length)
				throw new ArgumentOutOfRangeException("from", "Cannot copy data after the end of the buffer" +
				                                              "");
			if (_array == null)
			{
				fixed (byte* p = dest)
					NativeMethods.memcpy(p + offset, _pointer + from, count);
				return;
			}
			Buffer.BlockCopy(_array, from, dest, offset, count);
		}

		public void CopyTo(int from, byte* dest, int offset, int count)
		{
			if (from + count > Size)
				throw new ArgumentOutOfRangeException("from", "Cannot copy data after the end of the slice");

			if (_array == null)
			{
				NativeMethods.memcpy(dest + offset, _pointer + from, count);
				return;
			}

			fixed (byte* p = _array)
				NativeMethods.memcpy(dest + offset, p + from, count);
		}

		public Slice Clone()
		{
			var buffer = new byte[Size];
			if (_array == null)
			{
				fixed (byte* dest = buffer)
				{
					NativeMethods.memcpy(dest, _pointer, _size);
				}
			}
			else
			{
				Buffer.BlockCopy(_array, 0, buffer, 0, Size);
			}
			return new Slice(buffer);
		}

	    public ValueReader CreateReader()
	    {
            if(_array != null)
                return new ValueReader(_array, _size);

	        return new ValueReader(_pointer, _size);
	    }
	}
}