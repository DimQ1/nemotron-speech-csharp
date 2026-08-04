using System.Reflection;
var asm = typeof(Microsoft.Agents.AI.ChatClientAgent).Assembly;
var t = typeof(Microsoft.Agents.AI.ChatClientAgent);
Console.WriteLine("=== ChatClientAgent ctors ===");
foreach (var c in t.GetConstructors())
    Console.WriteLine("ctor(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
Console.WriteLine("=== ChatClientExtensions methods ===");
var ext = asm.GetType("Microsoft.Extensions.AI.ChatClientExtensions");
foreach (var m in ext!.GetMethods(BindingFlags.Public | BindingFlags.Static))
    Console.WriteLine(m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
Console.WriteLine("=== AIAgent RunAsync ===");
foreach (var m in typeof(Microsoft.Agents.AI.AIAgent).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.Contains("Run")))
    Console.WriteLine(m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
