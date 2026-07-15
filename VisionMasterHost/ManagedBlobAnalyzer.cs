using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace VisionMasterHost;

/// <summary>
/// 纯托管代码 Blob 候选。它不创建 VisionMaster 模块，因此不需要流程节点 ID，
/// 也不会与相机采集流程争用海康 SDK 内部状态。
/// </summary>
internal sealed class ManagedBlobCandidate
{
    public ManagedBlobCandidate(
        float centerX,
        float centerY,
        float rectangularity,
        float area,
        int left,
        int top,
        int width,
        int height)
    {
        CenterX = centerX;
        CenterY = centerY;
        Rectangularity = rectangularity;
        Area = area;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public float CenterX { get; }

    public float CenterY { get; }

    public float Rectangularity { get; }

    public float Area { get; }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }
}

internal static class ManagedBlobAnalyzer
{
    public static IReadOnlyList<ManagedBlobCandidate> Analyze(
        string imagePath,
        int expectedWidth,
        int expectedHeight)
    {
        using var source = new Bitmap(imagePath);
        if (source.Width != expectedWidth || source.Height != expectedHeight)
        {
            throw new InvalidDataException(
                $"拍照图尺寸与文件头不一致：文件={source.Width}×{source.Height}，" +
                $"预期={expectedWidth}×{expectedHeight}。");
        }

        // 统一为 24 位 BGR，避免相机输出 Mono8、索引色或其他像素格式时逐种分支。
        using var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var grayscale = ReadGrayscale(normalized);
        var threshold = CalculateOtsuThreshold(grayscale);
        var darkCandidates = FindConnectedComponents(
            grayscale,
            normalized.Width,
            normalized.Height,
            threshold,
            darkForeground: true);
        var brightCandidates = FindConnectedComponents(
            grayscale,
            normalized.Width,
            normalized.Height,
            threshold,
            darkForeground: false);

        return new[] { darkCandidates, brightCandidates }
            .OrderByDescending(candidates => candidates.Count >= 2)
            .ThenByDescending(ScoreCandidateSet)
            .ThenByDescending(candidates => candidates.Count)
            .First();
    }

    private static byte[] ReadGrayscale(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var grayscale = new byte[checked(width * height)];
        var bounds = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[checked(width * 3)];
            for (var y = 0; y < height; y++)
            {
                var rowPointer = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(rowPointer, row, 0, row.Length);
                var destinationOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var sourceOffset = x * 3;
                    var blue = row[sourceOffset];
                    var green = row[sourceOffset + 1];
                    var red = row[sourceOffset + 2];
                    grayscale[destinationOffset + x] =
                        (byte)((red * 77 + green * 150 + blue * 29 + 128) >> 8);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return grayscale;
    }

    private static byte CalculateOtsuThreshold(byte[] pixels)
    {
        var histogram = new long[256];
        long totalIntensity = 0;
        for (var index = 0; index < pixels.Length; index++)
        {
            var intensity = pixels[index];
            histogram[intensity]++;
            totalIntensity += intensity;
        }

        long backgroundCount = 0;
        long backgroundIntensity = 0;
        var bestThreshold = 0;
        var bestVariance = double.MinValue;
        for (var threshold = 0; threshold < histogram.Length; threshold++)
        {
            backgroundCount += histogram[threshold];
            if (backgroundCount == 0)
            {
                continue;
            }

            var foregroundCount = pixels.Length - backgroundCount;
            if (foregroundCount == 0)
            {
                break;
            }

            backgroundIntensity += threshold * histogram[threshold];
            var backgroundMean = backgroundIntensity / (double)backgroundCount;
            var foregroundMean =
                (totalIntensity - backgroundIntensity) / (double)foregroundCount;
            var meanDifference = backgroundMean - foregroundMean;
            var variance =
                backgroundCount * (double)foregroundCount * meanDifference * meanDifference;
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = threshold;
            }
        }

        return (byte)bestThreshold;
    }

    private static List<ManagedBlobCandidate> FindConnectedComponents(
        byte[] pixels,
        int width,
        int height,
        byte threshold,
        bool darkForeground)
    {
        var pixelCount = checked(width * height);
        var minimumArea = Math.Min(5000, Math.Max(40, pixelCount / 100000));
        var maximumArea = Math.Max(minimumArea + 1, pixelCount / 2);
        var maximumRunCount = Math.Min(2_000_000, Math.Max(100_000, pixelCount / 4));
        var allRuns = new List<BlobRun>();
        var previousRuns = new List<BlobRun>();
        var parents = new List<int>();
        var ranks = new List<byte>();

        // 使用逐行游程标记代替全图 BFS。每个像素只扫描一次，内存与前景游程数成正比，
        // 对 500 万到 2000 万像素的工业相机图远快于为每个像素检查 8 个邻居。
        for (var y = 0; y < height; y++)
        {
            var currentRuns = new List<BlobRun>();
            var rowOffset = y * width;
            var previousCursor = 0;
            var x = 0;
            while (x < width)
            {
                while (x < width &&
                       !IsForeground(pixels[rowOffset + x], threshold, darkForeground))
                {
                    x++;
                }
                if (x >= width)
                {
                    break;
                }

                var start = x;
                while (x + 1 < width &&
                       IsForeground(pixels[rowOffset + x + 1], threshold, darkForeground))
                {
                    x++;
                }

                var end = x;
                if (parents.Count >= maximumRunCount)
                {
                    throw new InvalidOperationException(
                        "图像前景碎片过多，已停止 Blob 分析以防内存耗尽；" +
                        "请检查相机噪声、光照或目标对比度。");
                }

                var label = parents.Count;
                parents.Add(label);
                ranks.Add(0);
                while (previousCursor < previousRuns.Count &&
                       previousRuns[previousCursor].End < start - 1)
                {
                    previousCursor++;
                }

                for (var previousIndex = previousCursor;
                     previousIndex < previousRuns.Count &&
                     previousRuns[previousIndex].Start <= end + 1;
                     previousIndex++)
                {
                    UnionLabels(parents, ranks, label, previousRuns[previousIndex].Label);
                }

                var run = new BlobRun(y, start, end, label);
                currentRuns.Add(run);
                allRuns.Add(run);
                x++;
            }

            previousRuns = currentRuns;
        }

        var components = new Dictionary<int, ComponentAccumulator>();
        foreach (var run in allRuns)
        {
            var root = FindRoot(parents, run.Label);
            if (!components.TryGetValue(root, out var component))
            {
                component = new ComponentAccumulator(width, height);
                components.Add(root, component);
            }

            component.Add(run);
        }

        return components.Values
            .Where(component => component.Area >= minimumArea && component.Area <= maximumArea)
            .Where(component => component.Width >= 3 && component.Height >= 3)
            // 同时触碰两条图像边的连通块通常是阈值后的背景，不是待抓取矩形。
            .Where(component => component.TouchedEdgeCount(width, height) < 2)
            .Select(component => component.ToCandidate())
            .Where(candidate => candidate.Rectangularity >= 0.35f)
            .OrderByDescending(ScoreCandidate)
            .Take(100)
            .ToList();
    }

    private static bool IsForeground(byte value, byte threshold, bool darkForeground)
    {
        return darkForeground ? value <= threshold : value > threshold;
    }

    private static double ScoreCandidateSet(IReadOnlyCollection<ManagedBlobCandidate> candidates)
    {
        return candidates
            .OrderByDescending(ScoreCandidate)
            .Take(2)
            .Sum(ScoreCandidate);
    }

    private static double ScoreCandidate(ManagedBlobCandidate candidate)
    {
        var rectangularity = Math.Max(0d, Math.Min(1d, candidate.Rectangularity));
        return rectangularity * rectangularity * rectangularity *
            Math.Log10(Math.Max(10d, candidate.Area));
    }

    private static int FindRoot(List<int> parents, int label)
    {
        var root = label;
        while (parents[root] != root)
        {
            root = parents[root];
        }

        while (parents[label] != label)
        {
            var next = parents[label];
            parents[label] = root;
            label = next;
        }

        return root;
    }

    private static void UnionLabels(
        List<int> parents,
        List<byte> ranks,
        int first,
        int second)
    {
        var firstRoot = FindRoot(parents, first);
        var secondRoot = FindRoot(parents, second);
        if (firstRoot == secondRoot)
        {
            return;
        }

        if (ranks[firstRoot] < ranks[secondRoot])
        {
            parents[firstRoot] = secondRoot;
        }
        else if (ranks[firstRoot] > ranks[secondRoot])
        {
            parents[secondRoot] = firstRoot;
        }
        else
        {
            parents[secondRoot] = firstRoot;
            ranks[firstRoot]++;
        }
    }

    private readonly struct BlobRun
    {
        public BlobRun(int y, int start, int end, int label)
        {
            Y = y;
            Start = start;
            End = end;
            Label = label;
        }

        public int Y { get; }

        public int Start { get; }

        public int End { get; }

        public int Label { get; }
    }

    private sealed class ComponentAccumulator
    {
        public ComponentAccumulator(int imageWidth, int imageHeight)
        {
            Left = imageWidth;
            Top = imageHeight;
            Right = -1;
            Bottom = -1;
        }

        public long Area { get; private set; }

        public long SumX { get; private set; }

        public long SumY { get; private set; }

        public int Left { get; private set; }

        public int Right { get; private set; }

        public int Top { get; private set; }

        public int Bottom { get; private set; }

        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;

        public void Add(BlobRun run)
        {
            var length = run.End - run.Start + 1;
            Area += length;
            SumX += (long)(run.Start + run.End) * length / 2;
            SumY += (long)run.Y * length;
            Left = Math.Min(Left, run.Start);
            Right = Math.Max(Right, run.End);
            Top = Math.Min(Top, run.Y);
            Bottom = Math.Max(Bottom, run.Y);
        }

        public int TouchedEdgeCount(int imageWidth, int imageHeight)
        {
            return (Left == 0 ? 1 : 0) +
                   (Right == imageWidth - 1 ? 1 : 0) +
                   (Top == 0 ? 1 : 0) +
                   (Bottom == imageHeight - 1 ? 1 : 0);
        }

        public ManagedBlobCandidate ToCandidate()
        {
            var boundingArea = (long)Width * Height;
            return new ManagedBlobCandidate(
                (float)(SumX / (double)Area),
                (float)(SumY / (double)Area),
                Area / (float)boundingArea,
                Area,
                Left,
                Top,
                Width,
                Height);
        }
    }
}
