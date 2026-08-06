// Coder CLI (.NET 11 port) — assembly-level declarations.

using System.Runtime.CompilerServices;

// The unit-test project (tests/unit) compiles the same src/ tree and needs to
// reach internal tool implementations (FileEdit, GlobMatch, …) to assert on them.
[assembly: InternalsVisibleTo("coder.tests")]
