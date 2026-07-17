namespace TcmInzenjering.Plugin.Roads.Terrain;

/// <summary>
/// Tokom TCM komandi Idle handleri ne smeju da otvaraju transakcije
/// (eInvalidContext na Transaction.Dispose / FATAL).
/// </summary>
internal static class TerrainCommandGuard
{
    private static int _depth;

    public static bool IsSuppressed => _depth > 0;

    public static IDisposable Suppress()
    {
        _depth++;
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_depth > 0)
            {
                _depth--;
            }
        }
    }
}
