using Microsoft.ML.OnnxRuntime;

namespace MangaFlow.App.Smoke;

/// <summary>dev CLI: ONNX 세션의 입력/출력 이름·-shape dump — greedy 루프 설계 전 IO 확인용.</summary>
public static class OnnxInfo
{
    public static int Run(string[] paths)
    {
        foreach (var p in paths)
        {
            Console.WriteLine($"==== {Path.GetFileName(p)}");
            using var s = new InferenceSession(p);
            Console.WriteLine("-- inputs");
            foreach (var i in s.InputMetadata)
                Console.WriteLine($"  {i.Key} : [{string.Join(",", i.Value.Dimensions)}] {i.Value.ElementType}");
            Console.WriteLine("-- outputs");
            foreach (var o in s.OutputMetadata)
                Console.WriteLine($"  {o.Key} : [{string.Join(",", o.Value.Dimensions)}] {o.Value.ElementType}");
        }
        return 0;
    }
}
