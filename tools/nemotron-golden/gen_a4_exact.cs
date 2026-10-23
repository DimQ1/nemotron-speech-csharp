#:property TargetFramework=net10.0
#:property PublishAot=false
// Re-creates A4's exp1 input bit-for-bit as float32: System.Random(7) + Box-Muller room tone sigma 0.002 (A4 exp1/Program.cs, Noise()),
//   lead 1 s | cv17/en/0001 | 10 s | cv17/en/0005 | 2 s tail      -> 415360 samples = 25.96 s, written as an IEEE-float WAV.
//   dotnet run gen_a4_exact.cs -- <TestAudioRoot> <out.wav>
using System.Text;
const int Rate = 16000;
float[] Noise(int n, float sigma, Random rng)
{
    var x = new float[n];
    for (int i = 0; i < n; i++)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        x[i] = (float)(sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
    return x;
}
float[] Concat(params float[][] parts) { var r = new float[parts.Sum(p => p.Length)]; int o = 0; foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; } return r; }
float[] Read(string path)
{
    var b = File.ReadAllBytes(path); int pos = 12;
    while (pos + 8 <= b.Length)
    {
        string id = Encoding.ASCII.GetString(b, pos, 4); int size = BitConverter.ToInt32(b, pos + 4);
        if (id == "data") { var x = new float[size / 2]; for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(b, pos + 8 + 2 * i) / 32768f; return x; }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException(path);
}
var rng = new Random(7);
var a = Read(Path.Combine(args[0], "cv17", "en", "0001.wav"));
var bb = Read(Path.Combine(args[0], "cv17", "en", "0005.wav"));
var audio = Concat(Noise(Rate, 0.002f, rng), a, Noise(10 * Rate, 0.002f, rng), bb, Noise(2 * Rate, 0.002f, rng));
using var w = new BinaryWriter(File.Create(args[1]));
int bytes = audio.Length * 4;
w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(16000); w.Write(64000); w.Write((short)4); w.Write((short)32);
w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
var buf = new byte[bytes]; Buffer.BlockCopy(audio, 0, buf, 0, bytes); w.Write(buf);
Console.WriteLine($"{audio.Length} samples");
