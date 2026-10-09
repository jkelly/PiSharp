// highlight.js 10.7.3 (BSD-3-Clause): lib/core.js (highlightAuto's results.sort) — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
namespace PiSharp.Cli.Interactive.Mode;

/// <summary>
/// V8's <c>Array.prototype.sort</c> (third_party/v8/builtins/array-sort.tq, a port of CPython's listsort). highlightAuto's
/// comparator is not a strict weak ordering (the <c>supersetOf</c> tie-break), so the winner depends on the exact
/// sequence of comparisons; this reproduces V8's TimSort step by step. Comparator results are used like V8 uses
/// <c>ToNumber(result)</c>: only <c>&lt; 0</c> / <c>&gt;= 0</c> tests (NaN fails both).
/// </summary>
internal static class HljsTimSort
{
    const int MinGallopWins = 7;

    public static void Sort<T>(T[] a, Func<T, T, double> compare) => new State<T>(a, compare).Run();

    sealed class State<T>(T[] a, Func<T, T, double> cmp)
    {
        readonly List<(int Base, int Length)> runs = [];
        int minGallop = MinGallopWins;

        public void Run()
        {
            var length = a.Length;
            if (length < 2) return;
            var remaining = length;
            var low = 0;
            var minRunLength = ComputeMinRunLength(remaining);
            while (remaining != 0)
            {
                var currentRunLength = CountAndMakeRun(low, low + remaining);
                if (currentRunLength < minRunLength)
                {
                    var forcedRunLength = Math.Min(minRunLength, remaining);
                    BinaryInsertionSort(low, low + currentRunLength, low + forcedRunLength);
                    currentRunLength = forcedRunLength;
                }
                runs.Add((low, currentRunLength));
                MergeCollapse();
                low += currentRunLength;
                remaining -= currentRunLength;
            }
            MergeForceCollapse();
        }

        static int ComputeMinRunLength(int n)
        {
            var r = 0;
            while (n >= 64)
            {
                r |= n & 1;
                n >>= 1;
            }
            return n + r;
        }

        int CountAndMakeRun(int lowArg, int high)
        {
            var low = lowArg + 1;
            if (low == high) return 1;
            var runLength = 2;
            var elementLow = a[low];
            var order = cmp(elementLow, a[low - 1]);
            var isDescending = order < 0;
            var previous = elementLow;
            for (var idx = low + 1; idx < high; ++idx)
            {
                var current = a[idx];
                order = cmp(current, previous);
                if (isDescending) { if (order >= 0) break; }
                else { if (order < 0) break; }
                previous = current;
                ++runLength;
            }
            if (isDescending) Array.Reverse(a, lowArg, runLength);
            return runLength;
        }

        void BinaryInsertionSort(int low, int startArg, int high)
        {
            var start = low == startArg ? startArg + 1 : startArg;
            for (; start < high; ++start)
            {
                var left = low;
                var right = start;
                var pivot = a[start];
                while (left < right)
                {
                    var mid = left + ((right - left) >> 1);
                    if (cmp(pivot, a[mid]) < 0) right = mid;
                    else left = mid + 1;
                }
                for (var p = start; p > left; --p) a[p] = a[p - 1];
                a[left] = pivot;
            }
        }

        bool RunInvariantEstablished(int n) => n < 2 || runs[n - 2].Length > runs[n - 1].Length + runs[n].Length;

        void MergeCollapse()
        {
            while (runs.Count > 1)
            {
                var n = runs.Count - 2;
                if (!RunInvariantEstablished(n + 1) || !RunInvariantEstablished(n))
                {
                    if (runs[n - 1].Length < runs[n + 1].Length) --n;
                    MergeAt(n);
                }
                else if (runs[n].Length <= runs[n + 1].Length) MergeAt(n);
                else break;
            }
        }

        void MergeForceCollapse()
        {
            while (runs.Count > 1)
            {
                var n = runs.Count - 2;
                if (n > 0 && runs[n - 1].Length < runs[n + 1].Length) --n;
                MergeAt(n);
            }
        }

        void MergeAt(int i)
        {
            var (baseA, lengthA) = runs[i];
            var (baseB, lengthB) = runs[i + 1];
            runs[i] = (baseA, lengthA + lengthB);
            runs.RemoveAt(i + 1);

            var k = GallopRight(a, a[baseB], baseA, lengthA, 0);
            baseA += k;
            lengthA -= k;
            if (lengthA == 0) return;
            lengthB = GallopLeft(a, a[baseA + lengthA - 1], baseB, lengthB, lengthB - 1);
            if (lengthB == 0) return;
            if (lengthA <= lengthB) MergeLow(baseA, lengthA, baseB, lengthB);
            else MergeHigh(baseA, lengthA, baseB, lengthB);
        }

        int GallopLeft(T[] array, T key, int @base, int length, int hint)
        {
            var lastOfs = 0;
            var offset = 1;
            var order = cmp(array[@base + hint], key);
            if (order < 0)
            {
                var maxOfs = length - hint;
                while (offset < maxOfs)
                {
                    order = cmp(array[@base + hint + offset], key);
                    if (order >= 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                lastOfs += hint;
                offset += hint;
            }
            else
            {
                var maxOfs = hint + 1;
                while (offset < maxOfs)
                {
                    order = cmp(array[@base + hint - offset], key);
                    if (order < 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                var tmp = lastOfs;
                lastOfs = hint - offset;
                offset = hint - tmp;
            }
            lastOfs++;
            while (lastOfs < offset)
            {
                var m = lastOfs + ((offset - lastOfs) >> 1);
                if (cmp(array[@base + m], key) < 0) lastOfs = m + 1;
                else offset = m;
            }
            return offset;
        }

        int GallopRight(T[] array, T key, int @base, int length, int hint)
        {
            var lastOfs = 0;
            var offset = 1;
            var order = cmp(key, array[@base + hint]);
            if (order < 0)
            {
                var maxOfs = hint + 1;
                while (offset < maxOfs)
                {
                    order = cmp(key, array[@base + hint - offset]);
                    if (order >= 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                var tmp = lastOfs;
                lastOfs = hint - offset;
                offset = hint - tmp;
            }
            else
            {
                var maxOfs = length - hint;
                while (offset < maxOfs)
                {
                    order = cmp(key, array[@base + hint + offset]);
                    if (order < 0) break;
                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0) offset = maxOfs;
                }
                if (offset > maxOfs) offset = maxOfs;
                lastOfs += hint;
                offset += hint;
            }
            lastOfs++;
            while (lastOfs < offset)
            {
                var m = lastOfs + ((offset - lastOfs) >> 1);
                if (cmp(key, array[@base + m]) < 0) offset = m;
                else lastOfs = m + 1;
            }
            return offset;
        }

        void MergeLow(int baseA, int lengthA, int baseB, int lengthB)
        {
            var temp = new T[lengthA];
            Array.Copy(a, baseA, temp, 0, lengthA);
            var dest = baseA;
            var cursorTemp = 0;
            var cursorB = baseB;
            a[dest++] = a[cursorB++];
            if (--lengthB == 0) goto Succeed;
            if (lengthA == 1) goto CopyB;
            while (true)
            {
                var winsA = 0;
                var winsB = 0;
                while (true)
                {
                    if (cmp(a[cursorB], temp[cursorTemp]) < 0)
                    {
                        a[dest++] = a[cursorB++];
                        ++winsB;
                        --lengthB;
                        winsA = 0;
                        if (lengthB == 0) goto Succeed;
                        if (winsB >= minGallop) break;
                    }
                    else
                    {
                        a[dest++] = temp[cursorTemp++];
                        ++winsA;
                        --lengthA;
                        winsB = 0;
                        if (lengthA == 1) goto CopyB;
                        if (winsA >= minGallop) break;
                    }
                }
                ++minGallop;
                var first = true;
                while (winsA >= MinGallopWins || winsB >= MinGallopWins || first)
                {
                    first = false;
                    minGallop = Math.Max(1, minGallop - 1);
                    winsA = GallopRight(temp, a[cursorB], cursorTemp, lengthA, 0);
                    if (winsA > 0)
                    {
                        Array.Copy(temp, cursorTemp, a, dest, winsA);
                        dest += winsA;
                        cursorTemp += winsA;
                        lengthA -= winsA;
                        if (lengthA == 1) goto CopyB;
                        if (lengthA == 0) goto Succeed;
                    }
                    a[dest++] = a[cursorB++];
                    if (--lengthB == 0) goto Succeed;
                    winsB = GallopLeft(a, temp[cursorTemp], cursorB, lengthB, 0);
                    if (winsB > 0)
                    {
                        Array.Copy(a, cursorB, a, dest, winsB);
                        dest += winsB;
                        cursorB += winsB;
                        lengthB -= winsB;
                        if (lengthB == 0) goto Succeed;
                    }
                    a[dest++] = temp[cursorTemp++];
                    if (--lengthA == 1) goto CopyB;
                }
                ++minGallop;
            }
        Succeed:
            if (lengthA > 0) Array.Copy(temp, cursorTemp, a, dest, lengthA);
            return;
        CopyB:
            Array.Copy(a, cursorB, a, dest, lengthB);
            a[dest + lengthB] = temp[cursorTemp];
        }

        void MergeHigh(int baseA, int lengthA, int baseB, int lengthB)
        {
            var temp = new T[lengthB];
            Array.Copy(a, baseB, temp, 0, lengthB);
            var dest = baseB + lengthB - 1;
            var cursorTemp = lengthB - 1;
            var cursorA = baseA + lengthA - 1;
            a[dest--] = a[cursorA--];
            if (--lengthA == 0) goto Succeed;
            if (lengthB == 1) goto CopyA;
            while (true)
            {
                var winsA = 0;
                var winsB = 0;
                while (true)
                {
                    if (cmp(temp[cursorTemp], a[cursorA]) < 0)
                    {
                        a[dest--] = a[cursorA--];
                        ++winsA;
                        --lengthA;
                        winsB = 0;
                        if (lengthA == 0) goto Succeed;
                        if (winsA >= minGallop) break;
                    }
                    else
                    {
                        a[dest--] = temp[cursorTemp--];
                        ++winsB;
                        --lengthB;
                        winsA = 0;
                        if (lengthB == 1) goto CopyA;
                        if (winsB >= minGallop) break;
                    }
                }
                ++minGallop;
                var first = true;
                while (winsA >= MinGallopWins || winsB >= MinGallopWins || first)
                {
                    first = false;
                    minGallop = Math.Max(1, minGallop - 1);
                    var k = GallopRight(a, temp[cursorTemp], baseA, lengthA, lengthA - 1);
                    winsA = lengthA - k;
                    if (winsA > 0)
                    {
                        dest -= winsA;
                        cursorA -= winsA;
                        Array.Copy(a, cursorA + 1, a, dest + 1, winsA);
                        lengthA -= winsA;
                        if (lengthA == 0) goto Succeed;
                    }
                    a[dest--] = temp[cursorTemp--];
                    if (--lengthB == 1) goto CopyA;
                    k = GallopLeft(temp, a[cursorA], 0, lengthB, lengthB - 1);
                    winsB = lengthB - k;
                    if (winsB > 0)
                    {
                        dest -= winsB;
                        cursorTemp -= winsB;
                        Array.Copy(temp, cursorTemp + 1, a, dest + 1, winsB);
                        lengthB -= winsB;
                        if (lengthB == 1) goto CopyA;
                        if (lengthB == 0) goto Succeed;
                    }
                    a[dest--] = a[cursorA--];
                    if (--lengthA == 0) goto Succeed;
                }
                ++minGallop;
            }
        Succeed:
            if (lengthB > 0) Array.Copy(temp, 0, a, dest - (lengthB - 1), lengthB);
            return;
        CopyA:
            dest -= lengthA;
            cursorA -= lengthA;
            Array.Copy(a, cursorA + 1, a, dest + 1, lengthA);
            a[dest] = temp[cursorTemp];
        }
    }
}
