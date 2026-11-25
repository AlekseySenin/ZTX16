using ZTX16.Core;

namespace ZTX16.Collections;

/// <summary>A last-in, first-out stack.</summary>
public sealed class Stack<T>
{
    private T[] _items = new T[4];
    private int _count;

    /// <summary>Number of items.</summary>
    public int Count => _count;

    /// <summary>Pushes an item.</summary>
    public void Push(T item)
    {
        if (_count == _items.Length)
        {
            var larger = new T[_items.Length * 2];
            for (int i = 0; i < _count; i++)
            {
                larger[i] = _items[i];
            }

            _items = larger;
        }

        _items[_count] = item;
        _count++;
    }

    /// <summary>Removes and returns the top item; stops the cartridge if the stack is empty.</summary>
    public T Pop()
    {
        if (_count == 0)
        {
            Intrinsics.Fail("Stack is empty");
        }

        _count--;
        T item = _items[_count];
        T empty = default!;
        _items[_count] = empty;
        return item;
    }

    /// <summary>Returns the top item; stops the cartridge if the stack is empty.</summary>
    public T Peek()
    {
        if (_count == 0)
        {
            Intrinsics.Fail("Stack is empty");
        }

        return _items[_count - 1];
    }

    /// <summary>Removes all items.</summary>
    public void Clear()
    {
        _items = new T[4];
        _count = 0;
    }
}

/// <summary>A first-in, first-out queue.</summary>
public sealed class Queue<T>
{
    private T[] _items = new T[4];
    private int _head;
    private int _count;

    /// <summary>Number of items.</summary>
    public int Count => _count;

    /// <summary>Adds an item at the end.</summary>
    public void Enqueue(T item)
    {
        if (_count == _items.Length)
        {
            var larger = new T[_items.Length * 2];
            for (int i = 0; i < _count; i++)
            {
                larger[i] = _items[(_head + i) % _items.Length];
            }

            _items = larger;
            _head = 0;
        }

        _items[(_head + _count) % _items.Length] = item;
        _count++;
    }

    /// <summary>Removes and returns the first item; stops the cartridge if the queue is empty.</summary>
    public T Dequeue()
    {
        if (_count == 0)
        {
            Intrinsics.Fail("Queue is empty");
        }

        T item = _items[_head];
        T empty = default!;
        _items[_head] = empty;
        _head = (_head + 1) % _items.Length;
        _count--;
        return item;
    }

    /// <summary>Returns the first item; stops the cartridge if the queue is empty.</summary>
    public T Peek()
    {
        if (_count == 0)
        {
            Intrinsics.Fail("Queue is empty");
        }

        return _items[_head];
    }

    /// <summary>Removes all items.</summary>
    public void Clear()
    {
        _items = new T[4];
        _head = 0;
        _count = 0;
    }
}
