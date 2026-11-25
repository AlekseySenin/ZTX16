using ZTX16.Core;

namespace ZTX16.Collections;

/// <summary>A growable list. Indexing outside 0..Count-1 stops the cartridge with a clear message.</summary>
public sealed class List<T> : System.Collections.IEnumerable
{
    private T[] _items;
    private int _count;

    /// <summary>Creates an empty list.</summary>
    public List()
    {
        _items = new T[4];
    }

    /// <summary>Creates an empty list with room for <paramref name="capacity"/> items.</summary>
    public List(int capacity)
    {
        _items = new T[capacity < 4 ? 4 : capacity];
    }

    /// <summary>Number of items.</summary>
    public int Count => _count;

    /// <summary>Number of items the list can hold before it grows.</summary>
    public int Capacity => _items.Length;

    /// <summary>Gets or sets the item at <paramref name="index"/>.</summary>
    public T this[int index]
    {
        get
        {
            CheckIndex(index);
            return _items[index];
        }

        set
        {
            CheckIndex(index);
            _items[index] = value;
        }
    }

    /// <summary>Appends an item.</summary>
    public void Add(T item)
    {
        if (_count == _items.Length)
        {
            Grow();
        }

        _items[_count] = item;
        _count++;
    }

    /// <summary>Inserts an item at <paramref name="index"/> (0..Count).</summary>
    public void Insert(int index, T item)
    {
        if (index < 0 || index > _count)
        {
            Intrinsics.Fail("List.Insert: index out of range");
        }

        if (_count == _items.Length)
        {
            Grow();
        }

        for (int i = _count; i > index; i--)
        {
            _items[i] = _items[i - 1];
        }

        _items[index] = item;
        _count++;
    }

    /// <summary>Removes the item at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        CheckIndex(index);
        _count--;
        for (int i = index; i < _count; i++)
        {
            _items[i] = _items[i + 1];
        }

        T empty = default!;
        _items[_count] = empty;
    }

    /// <summary>Removes the first occurrence of <paramref name="item"/>. Returns false if it was not found.</summary>
    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>Removes all items.</summary>
    public void Clear()
    {
        T empty = default!;
        for (int i = 0; i < _count; i++)
        {
            _items[i] = empty;
        }

        _count = 0;
    }

    /// <summary>Index of the first occurrence of <paramref name="item"/>, or -1.</summary>
    public int IndexOf(T item)
    {
        for (int i = 0; i < _count; i++)
        {
            if (Intrinsics.Equal(_items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True if the list contains <paramref name="item"/>.</summary>
    public bool Contains(T item) => IndexOf(item) >= 0;

    /// <summary>Reverses the order of the items.</summary>
    public void Reverse()
    {
        for (int i = 0, j = _count - 1; i < j; i++, j--)
        {
            T tmp = _items[i];
            _items[i] = _items[j];
            _items[j] = tmp;
        }
    }

    /// <summary>Copies the items into a new array.</summary>
    public T[] ToArray()
    {
        var result = new T[_count];
        for (int i = 0; i < _count; i++)
        {
            result[i] = _items[i];
        }

        return result;
    }

    /// <summary>Enumerator for <c>foreach</c>.</summary>
    public Enumerator GetEnumerator() => new(this);

    // Implemented only so C# allows collection initializers; never called by ZTX16 code.
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new System.NotSupportedException();

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            Intrinsics.Fail("List index out of range");
        }
    }

    private void Grow()
    {
        var larger = new T[_items.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            larger[i] = _items[i];
        }

        _items = larger;
    }

    /// <summary>Enumerates a list in order.</summary>
    public sealed class Enumerator
    {
        private readonly List<T> _list;
        private int _index = -1;

        internal Enumerator(List<T> list)
        {
            _list = list;
        }

        /// <summary>The current item.</summary>
        public T Current => _list._items[_index];

        /// <summary>Advances to the next item.</summary>
        public bool MoveNext()
        {
            _index++;
            return _index < _list._count;
        }
    }
}
