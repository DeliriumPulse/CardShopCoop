using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

var asmPath = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);

var module = new PEFile(asmPath);
var resolver = new UniversalAssemblyResolver(asmPath, false, module.Metadata.DetectTargetFrameworkId());
resolver.AddSearchDirectory(Path.GetDirectoryName(asmPath));

var settings = new DecompilerSettings(LanguageVersion.CSharp7_3)
{
    ThrowOnAssemblyResolveErrors = false,
    RemoveDeadCode = false,
    UseSdkStyleProjectFormat = false,
};
var decompiler = new WholeProjectDecompiler(settings, resolver, resolver, null, null);
decompiler.DecompileProject(module, outDir);
Console.WriteLine($"Decompiled {asmPath} -> {outDir}");
