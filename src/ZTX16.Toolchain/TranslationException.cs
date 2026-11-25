namespace ZTX16.Toolchain;

/// <summary>
/// The cartridge uses something the ZTX16 toolchain cannot translate (yet), or violates
/// the cartridge contract. The message names the method and IL offset where possible.
/// </summary>
public sealed class TranslationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TranslationException(string message)
        : base(message)
    {
    }
}
