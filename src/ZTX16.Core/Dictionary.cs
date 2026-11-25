using ZTX16.Core;

namespace ZTX16.Collections;

/// <summary>
/// A hash map. Keys compare by value for numbers and strings and by identity for other
/// objects. <see cref="Keys"/> and <see cref="Values"/> list entries in insertion order
/// (slots freed by <see cref="Remove"/> are reused).
/// </summary>
public sealed class Dictionary<TKey, TValue> : System.Collections.IEnumerable
{
    private int[] _buckets = new int[0];   // entry index + 1; 0 = empty
    private int[] _next = new int[0];      // entry index + 1 of the next entry in the chain or free list
    private int[] _hashes = new int[0];
    private bool[] _used = new bool[0];
    private TKey[] _keys = new TKey[0];
    private TValue[] _values = new TValue[0];
    private int _count;
    private int _end;
    private int _free;                      // entry index + 1 of the first free slot

    /// <summary>Creates an empty dictionary.</summary>
    public Dictionary()
    {
        Allocate(8);
    }

    /// <summary>Number of entries.</summary>
    public int Count => _count;

    /// <summary>Gets the value for <paramref name="key"/> (stops the cartridge if missing) or sets it.</summary>
    public TValue this[TKey key]
    {
        get
        {
            int index = Find(key);
            if (index < 0)
            {
                Intrinsics.Fail("Dictionary: key not found");
            }

            return _values[index];
        }

        set => Insert(key, value, overwrite: true);
    }

    /// <summary>Adds an entry; stops the cartridge if the key already exists.</summary>
    public void Add(TKey key, TValue value) => Insert(key, value, overwrite: false);

    /// <summary>True if the dictionary contains <paramref name="key"/>.</summary>
    public bool ContainsKey(TKey key) => Find(key) >= 0;

    /// <summary>Returns the value for <paramref name="key"/>, or <paramref name="fallback"/> if missing.</summary>
    public TValue GetValueOrDefault(TKey key, TValue fallback)
    {
        int index = Find(key);
        return index < 0 ? fallback : _values[index];
    }

    /// <summary>Removes the entry for <paramref name="key"/>. Returns false if it was not present.</summary>
    public bool Remove(TKey key)
    {
        int hash = Intrinsics.Hash(key);
        int bucket = Bucket(hash, _buckets.Length);
        int previous = -1;
        for (int i = _buckets[bucket] - 1; i >= 0; i = _next[i] - 1)
        {
            if (_hashes[i] == hash && Intrinsics.Equal(_keys[i], key))
            {
                if (previous < 0)
                {
                    _buckets[bucket] = _next[i];
                }
                else
                {
                    _next[previous] = _next[i];
                }

                TKey emptyKey = default!;
                TValue emptyValue = default!;
                _keys[i] = emptyKey;
                _values[i] = emptyValue;
                _used[i] = false;
                _next[i] = _free;
                _free = i + 1;
                _count--;
                return true;
            }

            previous = i;
        }

        return false;
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        Allocate(8);
        _count = 0;
        _end = 0;
        _free = 0;
    }

    /// <summary>The keys, in insertion order (a new list).</summary>
    public List<TKey> Keys
    {
        get
        {
            var result = new List<TKey>(_count);
            for (int i = 0; i < _end; i++)
            {
                if (_used[i])
                {
                    result.Add(_keys[i]);
                }
            }

            return result;
        }
    }

    /// <summary>The values, in insertion order (a new list).</summary>
    public List<TValue> Values
    {
        get
        {
            var result = new List<TValue>(_count);
            for (int i = 0; i < _end; i++)
            {
                if (_used[i])
                {
                    result.Add(_values[i]);
                }
            }

            return result;
        }
    }

    // Implemented only so C# allows collection initializers; never called by ZTX16 code.
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new System.NotSupportedException();

    private int Find(TKey key)
    {
        int hash = Intrinsics.Hash(key);
        for (int i = _buckets[Bucket(hash, _buckets.Length)] - 1; i >= 0; i = _next[i] - 1)
        {
            if (_hashes[i] == hash && Intrinsics.Equal(_keys[i], key))
            {
                return i;
            }
        }

        return -1;
    }

    private void Insert(TKey key, TValue value, bool overwrite)
    {
        int existing = Find(key);
        if (existing >= 0)
        {
            if (!overwrite)
            {
                Intrinsics.Fail("Dictionary: an entry with the same key already exists");
            }

            _values[existing] = value;
            return;
        }

        int index;
        if (_free > 0)
        {
            index = _free - 1;
            _free = _next[index];
        }
        else
        {
            if (_end == _keys.Length)
            {
                Resize(_keys.Length * 2);
            }

            index = _end;
            _end++;
        }

        int hash = Intrinsics.Hash(key);
        int bucket = Bucket(hash, _buckets.Length);
        _keys[index] = key;
        _values[index] = value;
        _hashes[index] = hash;
        _used[index] = true;
        _next[index] = _buckets[bucket];
        _buckets[bucket] = index + 1;
        _count++;
    }

    private void Allocate(int capacity)
    {
        _buckets = new int[capacity];
        _next = new int[capacity];
        _hashes = new int[capacity];
        _used = new bool[capacity];
        _keys = new TKey[capacity];
        _values = new TValue[capacity];
    }

    private void Resize(int capacity)
    {
        int[] hashes = _hashes;
        bool[] used = _used;
        TKey[] keys = _keys;
        TValue[] values = _values;
        Allocate(capacity);
        for (int i = 0; i < _end; i++)
        {
            _hashes[i] = hashes[i];
            _used[i] = used[i];
            _keys[i] = keys[i];
            _values[i] = values[i];
            if (used[i])
            {
                int bucket = Bucket(hashes[i], capacity);
                _next[i] = _buckets[bucket];
                _buckets[bucket] = i + 1;
            }
        }

        // Only reached when every slot is in use, so the free list is empty.
        _free = 0;
    }

    private static int Bucket(int hash, int length) => (hash & 0x7FFFFFFF) % length;
}

/// <summary>A set of unique values, built on <see cref="Dictionary{TKey, TValue}"/>.</summary>
public sealed class HashSet<T> : System.Collections.IEnumerable
{
    private readonly Dictionary<T, bool> _items = new();

    /// <summary>Number of values.</summary>
    public int Count => _items.Count;

    /// <summary>Adds a value. Returns false if it was already present.</summary>
    public bool Add(T item)
    {
        if (_items.ContainsKey(item))
        {
            return false;
        }

        _items.Add(item, true);
        return true;
    }

    /// <summary>True if the set contains <paramref name="item"/>.</summary>
    public bool Contains(T item) => _items.ContainsKey(item);

    /// <summary>Removes a value. Returns false if it was not present.</summary>
    public bool Remove(T item) => _items.Remove(item);

    /// <summary>Removes all values.</summary>
    public void Clear() => _items.Clear();

    /// <summary>The values, in insertion order (a new list).</summary>
    public List<T> ToList() => _items.Keys;

    // Implemented only so C# allows collection initializers; never called by ZTX16 code.
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new System.NotSupportedException();
}
