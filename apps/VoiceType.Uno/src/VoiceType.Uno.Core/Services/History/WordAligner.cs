namespace VoiceType.Uno.History;

public enum DiffKind
{
    Equal,
    Insert,
    Delete,
    Replace,
}

/// <summary>A run of tokens: <c>A[AStart..AStart+ACount)</c> became <c>B[BStart..BStart+BCount)</c>.</summary>
public readonly record struct DiffHunk(DiffKind Kind, int AStart, int ACount, int BStart, int BCount);

/// <summary>
/// Minimal edit script between two token sequences (Myers O(ND), common prefix/suffix
/// trimmed first). Above <see cref="DefaultMaxEditDistance"/> edits the middle part is
/// reported as one replace hunk instead of running out of memory on two unrelated
/// hour-long transcripts.
/// </summary>
public static class WordAligner
{
    public const int DefaultMaxEditDistance = 2000;

    public static IReadOnlyList<DiffHunk> Align(
        IReadOnlyList<string> a,
        IReadOnlyList<string> b,
        int maxEditDistance = DefaultMaxEditDistance)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var ops = new List<DiffKind>(a.Count + b.Count);

        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix
               && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
            suffix++;

        for (var i = 0; i < prefix; i++)
            ops.Add(DiffKind.Equal);

        var n = a.Count - prefix - suffix;
        var m = b.Count - prefix - suffix;
        if (n == 0)
        {
            for (var i = 0; i < m; i++) ops.Add(DiffKind.Insert);
        }
        else if (m == 0)
        {
            for (var i = 0; i < n; i++) ops.Add(DiffKind.Delete);
        }
        else if (!TryMyers(a, b, prefix, n, m, maxEditDistance, ops))
        {
            for (var i = 0; i < n; i++) ops.Add(DiffKind.Delete);
            for (var i = 0; i < m; i++) ops.Add(DiffKind.Insert);
        }

        for (var i = 0; i < suffix; i++)
            ops.Add(DiffKind.Equal);

        return ToHunks(ops);
    }

    private static bool TryMyers(
        IReadOnlyList<string> a, IReadOnlyList<string> b, int start, int n, int m,
        int maxEditDistance, List<DiffKind> ops)
    {
        var max = n + m;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;

        for (var d = 0; d <= max; d++)
        {
            if (d > maxEditDistance)
                return false;

            // Snapshot of v[-d-1 .. d+1] as it was before step d (what backtracking reads).
            var snapshot = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);

            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    x = v[offset + k + 1];
                else
                    x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[start + x] == b[start + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            if (found >= 0)
                break;
        }

        // Backtrack from (n, m) to (0, 0), collecting operations in reverse.
        var reversed = new List<DiffKind>(n + m);
        int cx = n, cy = m;
        for (var d = found; d >= 0; d--)
        {
            var snap = trace[d];
            int At(int k) => snap[k + d + 1];

            var k = cx - cy;
            var prevK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            var prevX = At(prevK);
            var prevY = prevX - prevK;

            while (cx > prevX && cy > prevY)
            {
                reversed.Add(DiffKind.Equal);
                cx--;
                cy--;
            }
            if (d > 0)
            {
                if (cx == prevX)
                {
                    reversed.Add(DiffKind.Insert);
                    cy--;
                }
                else
                {
                    reversed.Add(DiffKind.Delete);
                    cx--;
                }
            }
        }

        reversed.Reverse();
        ops.AddRange(reversed);
        return true;
    }

    private static List<DiffHunk> ToHunks(List<DiffKind> ops)
    {
        var hunks = new List<DiffHunk>();
        int ai = 0, bi = 0, i = 0;
        while (i < ops.Count)
        {
            if (ops[i] == DiffKind.Equal)
            {
                int a0 = ai, b0 = bi;
                while (i < ops.Count && ops[i] == DiffKind.Equal) { ai++; bi++; i++; }
                hunks.Add(new DiffHunk(DiffKind.Equal, a0, ai - a0, b0, bi - b0));
            }
            else
            {
                int a0 = ai, b0 = bi;
                while (i < ops.Count && ops[i] != DiffKind.Equal)
                {
                    if (ops[i] == DiffKind.Delete) ai++; else bi++;
                    i++;
                }
                var kind = (ai - a0, bi - b0) switch
                {
                    (> 0, > 0) => DiffKind.Replace,
                    (> 0, _) => DiffKind.Delete,
                    _ => DiffKind.Insert,
                };
                hunks.Add(new DiffHunk(kind, a0, ai - a0, b0, bi - b0));
            }
        }
        return hunks;
    }
}
