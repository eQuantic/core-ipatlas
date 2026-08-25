namespace eQuantic.IpAtlas.Compiler;

/// <summary>
/// Opens a file for appending after removing anything a previous run left
/// half-written.
/// <para>
/// A killed process does not stop between lines. A StreamWriter flushes when
/// its buffer fills, which can be in the middle of one, so the file can end
/// without a newline. Appending to that joins the truncated line to the next
/// good one and produces a row that looks real and is not — a resume file that
/// records "lacregistro.br" for a block that belongs to neither.
/// </para>
/// <para>
/// The partial line is dropped rather than terminated. It is a fragment of a
/// record nobody can complete, and leaving it behind means every reader has to
/// know that.
/// </para>
/// </summary>
public static class AppendSafely
{
    /// <summary>Trims any trailing partial line, then opens the file for appending.</summary>
    public static StreamWriter Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        TrimPartialLine(path);
        return new StreamWriter(path, append: true);
    }

    /// <summary>Removes a final line that has no newline after it.</summary>
    public static void TrimPartialLine(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        if (file.Length == 0)
        {
            return;
        }

        file.Seek(-1, SeekOrigin.End);
        if (file.ReadByte() == '\n')
        {
            return;
        }

        // Walk back to the last newline; everything after it is a fragment.
        var buffer = new byte[4096];
        var position = file.Length;
        while (position > 0)
        {
            var take = (int)Math.Min(buffer.Length, position);
            position -= take;
            file.Seek(position, SeekOrigin.Begin);
            file.ReadExactly(buffer, 0, take);

            for (var i = take - 1; i >= 0; i--)
            {
                if (buffer[i] == '\n')
                {
                    file.SetLength(position + i + 1);
                    return;
                }
            }
        }

        // No newline anywhere: the whole file is one unfinished line.
        file.SetLength(0);
    }
}
