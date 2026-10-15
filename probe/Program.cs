using System;
using System.Reflection;

var t = typeof(Avalonia.AppBuilder);
Console.WriteLine("=== AppBuilder methods mentioning Rendering/Software/Skia ===");
foreach (var m in t.GetMethods())
{
    if (m.Name.Contains("Rendering") || m.Name.Contains("Software") || m.Name.Contains("Skia"))
        Console.WriteLine(m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name)) + ")");
}

Console.WriteLine("=== RenderingProfile type members ===");
foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
{
    foreach (var ty in a.GetTypes())
    {
        if (ty.Name.Contains("RenderingProfile"))
        {
            Console.WriteLine(ty.FullName);
            foreach (var f in ty.GetFields()) Console.WriteLine("  field " + f.FieldType.Name + " " + f.Name);
            foreach (var p in ty.GetProperties()) Console.WriteLine("  prop " + p.PropertyType.Name + " " + p.Name);
        }
    }
}
