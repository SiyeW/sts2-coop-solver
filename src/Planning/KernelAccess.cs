using System;

// Use our local attribute, not the inaccessible internal copy in the release.
#pragma warning disable CS0436
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("CoopBots.Kernel")]
#pragma warning restore CS0436

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
{
    public string AssemblyName { get; } = assemblyName;
}
