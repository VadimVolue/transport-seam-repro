using System;

namespace TransportSeamRepro
{
    internal static class Output
    {
        internal static void Log(string area, string message)
        {
            Console.WriteLine($"[{area}] {message}");
        }

        internal static void Blank()
        {
            Console.WriteLine();
        }

        internal static void Line(string text)
        {
            Console.WriteLine(text);
        }
    }
}
