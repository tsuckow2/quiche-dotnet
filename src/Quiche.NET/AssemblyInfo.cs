// The test project exercises the raw Native layer directly (loader
// resolution, generated signatures), so it needs internal access.
// The public managed API (step 3) is tested through its public
// surface; only the step-2 smoke tests reach into Native/.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Quiche.NET.Tests")]
