using System.IO.MemoryMappedFiles;

namespace KPACS.Viewer.Rendering;

internal abstract class VolumeVoxelBuffer : IDisposable
{
    public abstract int Length { get; }

    public abstract string? SharedMapName { get; }

    public abstract short this[int index] { get; set; }

    public abstract Span<short> GetSpan();

    public virtual bool TryGetArray(out short[]? array)
    {
        array = null;
        return false;
    }

    public virtual short[] GetOrCreateArrayCopy() => GetSpan().ToArray();

    public void CopyTo(int sourceIndex, short[] destination, int destinationIndex, int length)
    {
        GetSpan().Slice(sourceIndex, length).CopyTo(destination.AsSpan(destinationIndex, length));
    }

    public static VolumeVoxelBuffer FromArray(short[] voxels) => new ArrayVolumeVoxelBuffer(voxels);

    public static VolumeVoxelBuffer CreatePreferred(int voxelCount)
    {
        if (OperatingSystem.IsWindows() && voxelCount > 0)
        {
            return SharedMemoryVolumeVoxelBuffer.Create(voxelCount);
        }

        return new ArrayVolumeVoxelBuffer(new short[voxelCount]);
    }

    public virtual void Dispose()
    {
    }

    private sealed class ArrayVolumeVoxelBuffer(short[] voxels) : VolumeVoxelBuffer
    {
        public override int Length => voxels.Length;

        public override string? SharedMapName => null;

        public override short this[int index]
        {
            get => voxels[index];
            set => voxels[index] = value;
        }

        public override Span<short> GetSpan() => voxels.AsSpan();

        public override bool TryGetArray(out short[]? array)
        {
            array = voxels;
            return true;
        }

        public override short[] GetOrCreateArrayCopy() => voxels;
    }

    private sealed unsafe class SharedMemoryVolumeVoxelBuffer : VolumeVoxelBuffer
    {
        private readonly int _length;
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly string _mapName;
        private byte* _pointer;
        private short[]? _cachedArrayCopy;
        private bool _disposed;

        private SharedMemoryVolumeVoxelBuffer(int length, string mapName, MemoryMappedFile mapping, MemoryMappedViewAccessor accessor)
        {
            _length = length;
            _mapName = mapName;
            _mapping = mapping;
            _accessor = accessor;

            byte* pointer = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointer = pointer + _accessor.PointerOffset;
        }

        public static SharedMemoryVolumeVoxelBuffer Create(int voxelCount)
        {
            long capacityBytes = checked((long)voxelCount * sizeof(short));
            string mapName = $@"Local\kpacs-vol-{Guid.NewGuid():N}";
            MemoryMappedFile mapping = MemoryMappedFile.CreateNew(
                mapName,
                capacityBytes,
                MemoryMappedFileAccess.ReadWrite,
                MemoryMappedFileOptions.None,
                HandleInheritability.None);
            MemoryMappedViewAccessor accessor = mapping.CreateViewAccessor(0, capacityBytes, MemoryMappedFileAccess.ReadWrite);
            return new SharedMemoryVolumeVoxelBuffer(voxelCount, mapName, mapping, accessor);
        }

        public override int Length => _length;

        public override string SharedMapName => _mapName;

        public override short this[int index]
        {
            get => ((short*)_pointer)[index];
            set => ((short*)_pointer)[index] = value;
        }

        public override Span<short> GetSpan() => new(_pointer, _length);

        public override short[] GetOrCreateArrayCopy() => _cachedArrayCopy ??= GetSpan().ToArray();

        public override void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~SharedMemoryVolumeVoxelBuffer()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            if (disposing)
            {
                _accessor.Dispose();
                _mapping.Dispose();
            }

            _pointer = null;
            _disposed = true;
        }
    }
}