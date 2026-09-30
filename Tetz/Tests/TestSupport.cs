// Stands in for what upstream's test project gets from ImplicitUsings and from being a separate
// assembly. Global usings apply to the whole test assembly, so only ones the mod sources can live with.
global using System.Collections.Generic;
global using System.Linq;
global using Xunit;
global using InvalidOperationException = System.InvalidOperationException;

// The mod's types are internal; Moq's proxies need to see them.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
