namespace ZTX16.VM;

/// <summary>A heap object. References are handles (index + 1); 0 is null.</summary>
internal abstract class HeapObject
{
    public bool Marked { get; set; }

    /// <summary>Approximate size in bytes, used for heap accounting.</summary>
    public abstract long Size { get; }
}

/// <summary>An instance of a class: one slot per field.</summary>
internal sealed class InstanceObject(int classIndex, int fieldCount) : HeapObject
{
    public int Class { get; } = classIndex;

    public long[] Fields { get; } = new long[fieldCount];

    public override long Size => 32 + (8L * Fields.Length);
}

/// <summary>A one-dimensional array with typed storage.</summary>
internal sealed class ArrayObject : HeapObject
{
    public ArrayObject(VmArrayKind kind, int length)
    {
        Kind = kind;
        Length = length;
        Data = kind switch
        {
            VmArrayKind.Bits8 => new byte[length],
            VmArrayKind.Bits16 => new ushort[length],
            VmArrayKind.Bits32 => new int[length],
            VmArrayKind.Bits64 or VmArrayKind.Ref => new long[length],
            VmArrayKind.F32 => new float[length],
            _ => new double[length],
        };
    }

    public VmArrayKind Kind { get; }

    public int Length { get; }

    public Array Data { get; }

    public override long Size => 32 + ((long)Length * Kind switch
    {
        VmArrayKind.Bits8 => 1,
        VmArrayKind.Bits16 => 2,
        VmArrayKind.Bits32 or VmArrayKind.F32 => 4,
        _ => 8,
    });
}

/// <summary>What a <see cref="PointerObject"/> points at.</summary>
internal enum PointerKind : byte
{
    /// <summary>Its own value slot: a heap-allocated variable.</summary>
    Cell,

    /// <summary>A field slot of an instance.</summary>
    Field,

    /// <summary>An array element.</summary>
    Element,

    /// <summary>A module global.</summary>
    Global,
}

/// <summary>A managed pointer to a non-struct location (ref/out parameters, ref locals).</summary>
internal sealed class PointerObject : HeapObject
{
    public PointerKind Kind { get; init; }

    /// <summary>Handle of the instance or array pointed into (Field, Element).</summary>
    public long Target { get; init; }

    /// <summary>Field slot, element index or global index.</summary>
    public int Index { get; init; }

    /// <summary>Contents of a cell.</summary>
    public long Value { get; set; }

    /// <summary>Element access describing the pointed-to location: its type and how stores narrow.</summary>
    public VmElementAccess Access { get; init; }

    public override long Size => 48;
}

/// <summary>An immutable string of UTF-16 code units.</summary>
internal sealed class StringObject(string value) : HeapObject
{
    public string Value { get; } = value;

    public override long Size => 32 + (2L * Value.Length);
}

/// <summary>
/// The managed heap: handle table, accounting and a non-moving mark-sweep collector.
/// Root enumeration is the instance's job; the heap traces from the roots it is given.
/// </summary>
internal sealed class VmHeap
{
    /// <summary>Maximum live heap size. Provisional implementation limit.</summary>
    public const long LimitBytes = 32L * 1024 * 1024;

    /// <summary>Allocation volume between automatic collections. Provisional.</summary>
    public const long CollectionThresholdBytes = 4L * 1024 * 1024;

    private readonly List<HeapObject?> _objects = [];
    private readonly Stack<int> _free = new();
    private readonly Stack<HeapObject> _markStack = new();
    private readonly VmModule _module;
    private long _allocatedSinceCollection;

    public VmHeap(VmModule module)
    {
        _module = module;
    }

    /// <summary>Approximate bytes held by live (and not yet collected) objects.</summary>
    public long LiveBytes { get; private set; }

    /// <summary>Number of objects currently in the handle table.</summary>
    public int ObjectCount => _objects.Count - _free.Count;

    /// <summary>Number of completed collections.</summary>
    public int Collections { get; private set; }

    /// <summary>True if allocating <paramref name="size"/> bytes should first trigger a collection.</summary>
    public bool ShouldCollectBefore(long size) =>
        _allocatedSinceCollection + size > CollectionThresholdBytes || LiveBytes + size > LimitBytes;

    /// <summary>True if <paramref name="size"/> more bytes fit under the limit.</summary>
    public bool Fits(long size) => LiveBytes + size <= LimitBytes;

    /// <summary>Stores <paramref name="obj"/> and returns its handle.</summary>
    public long Add(HeapObject obj)
    {
        int index;
        if (_free.TryPop(out int reused))
        {
            index = reused;
            _objects[index] = obj;
        }
        else
        {
            index = _objects.Count;
            _objects.Add(obj);
        }

        LiveBytes += obj.Size;
        _allocatedSinceCollection += obj.Size;
        return index + 1L;
    }

    /// <summary>Returns the object for a non-null handle.</summary>
    public HeapObject Get(long handle) => _objects[(int)(handle - 1)]!;

    /// <summary>Marks the object referenced by <paramref name="handle"/> (0 is ignored).</summary>
    public void MarkRoot(long handle)
    {
        if (handle != 0)
        {
            Push(Get(handle));
        }
    }

    /// <summary>Traces from the marked roots, then frees every unmarked object.</summary>
    public void TraceAndSweep()
    {
        while (_markStack.TryPop(out HeapObject? obj))
        {
            switch (obj)
            {
                case InstanceObject instance:
                    IReadOnlyList<VmType> types = _module.Classes[instance.Class].Fields;
                    for (int i = 0; i < types.Count; i++)
                    {
                        if (types[i] == VmType.Ref && instance.Fields[i] != 0)
                        {
                            Push(Get(instance.Fields[i]));
                        }
                    }

                    break;

                case PointerObject pointer:
                    if (pointer.Kind is PointerKind.Field or PointerKind.Element)
                    {
                        Push(Get(pointer.Target));
                    }
                    else if (pointer.Kind == PointerKind.Cell && pointer.Access == VmElementAccess.Ref && pointer.Value != 0)
                    {
                        Push(Get(pointer.Value));
                    }

                    break;

                case ArrayObject { Kind: VmArrayKind.Ref } array:
                    foreach (long element in (long[])array.Data)
                    {
                        if (element != 0)
                        {
                            Push(Get(element));
                        }
                    }

                    break;
            }
        }

        for (int i = 0; i < _objects.Count; i++)
        {
            HeapObject? obj = _objects[i];
            if (obj is null)
            {
                continue;
            }

            if (obj.Marked)
            {
                obj.Marked = false;
            }
            else
            {
                LiveBytes -= obj.Size;
                _objects[i] = null;
                _free.Push(i);
            }
        }

        _allocatedSinceCollection = 0;
        Collections++;
    }

    private void Push(HeapObject obj)
    {
        if (!obj.Marked)
        {
            obj.Marked = true;
            _markStack.Push(obj);
        }
    }
}
