namespace EQLOverlay.Services;

/// <summary>One tick of a full-log replay (Data → Reparse / Merge / Reset &amp;
/// rebuild): which file, how far through it by bytes, how many lines so far.
/// Owner request (14 Sep): "would be nice with a reparse log progress bar —
/// atm it's just a spinning cursor". Fraction and the card's texts are pure
/// so the selftest can pin them.</summary>
public sealed record ReparseProgress(string File, int FileIndex, int FileCount,
    long BytesDone, long BytesTotal, long Lines, bool Done = false, string Verb = "Replaying")
{
    /// <summary>0..1 by bytes; an empty file reads 0 until done, then 1.</summary>
    public double Fraction => BytesTotal <= 0
        ? (Done ? 1 : 0)
        : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);

    /// <summary>"43%" — hand-built so Danish culture doesn't render "43 %".</summary>
    public string Percent => $"{(int)Math.Round(Fraction * 100)}%";

    /// <summary>"Replaying eqlog_X.txt" — "… — file 2 of 3" when a rebuild walks
    /// several; the startup catch-up says "Catching up eqlog_X.txt".</summary>
    public string Title => FileCount > 1
        ? $"{Verb} {File} — file {FileIndex} of {FileCount}"
        : $"{Verb} {File}";

    /// <summary>"213,400 lines replayed" (culture separators — display only).</summary>
    public string Detail => Done ? $"{Lines:N0} lines replayed — done" : $"{Lines:N0} lines replayed";
}
