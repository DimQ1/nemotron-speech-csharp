using System.Reflection;
var asm = typeof(Microsoft.ML.Tokenizers.SentencePieceTokenizer).Assembly;
var t = typeof(Microsoft.ML.Tokenizers.SentencePieceTokenizer);
foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
    Console.WriteLine(m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
Console.WriteLine("--- EncodeToIds instance ---");
foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "EncodeToIds"))
    Console.WriteLine("(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
