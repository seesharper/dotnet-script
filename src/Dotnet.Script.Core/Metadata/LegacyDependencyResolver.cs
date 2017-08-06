using System.Collections.Generic;

namespace Dotnet.Script.Core.Metadata
{
    /// <summary>
    /// An <see cref="IDependencyResolver"/> that resolves runtime dependencies 
    /// from an "project.json" file.
    /// </summary>
    public class LegacyDependencyResolver : IDependencyResolver
    {
        public IEnumerable<RuntimeDependency> GetRuntimeDependencies(string projectFile)
        {
            throw new System.NotImplementedException();
        }
    }
}