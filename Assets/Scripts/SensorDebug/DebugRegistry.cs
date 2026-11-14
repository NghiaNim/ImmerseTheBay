using System;
using System.Collections.Generic;
using System.Text;

namespace ImmerseTheBay
{
    /// <summary>Any module can register a text provider here; the HUD prints all of them.</summary>
    public static class DebugRegistry
    {
        static readonly List<Func<string>> s_Providers = new();
        static readonly StringBuilder s_Sb = new();

        public static void Register(Func<string> provider) { if (!s_Providers.Contains(provider)) s_Providers.Add(provider); }
        public static void Unregister(Func<string> provider) => s_Providers.Remove(provider);

        public static string Build()
        {
            s_Sb.Clear();
            foreach (var p in s_Providers)
            {
                try { s_Sb.AppendLine(p()); }
                catch (Exception e) { s_Sb.AppendLine($"<color=#f88>{e.GetType().Name}: {e.Message}</color>"); }
            }
            return s_Sb.ToString();
        }
    }
}
