using System.Runtime.InteropServices;

// Toda DLL nativa chamada por P/Invoke vem do System32, nunca da pasta do executável (evita DLL plantada ao lado do Leve).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
