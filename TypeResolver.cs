using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TypeResolver
{
  /// <summary>
  /// A resolver for types using a hybrid of the C# style and CLR resolution style.
  /// </summary>
  /// <remarks>
  /// By default this uses C# style, but any type wrapped in brackets [] will use CLR resolution style.
  /// CLR resolution style may require the name to be fully qualified,
  /// but it also may access any type in the system (including outside of well known paths).
  /// The entire input into the resolver may be wrapped in a [] to use CLR resolution over the whole string.
  /// </remarks>
  public class TypeResolver
  {
    /// <summary>
    /// The regex for the TypeResolver generic format.
    /// </summary>
    private static readonly Regex TypeFormatRegex = new Regex("^(?<Name>[^<>?]+)(<(?<generics>.+)>)?$");

    /// <summary>
    /// The well known types to use short cuts to define.
    /// These are types that do not exist in the type system,
    /// but where the names are well known such as int or float.
    /// </summary>
    public Dictionary<string, Type> WellKnownTypes { get; } = new Dictionary<string, Type>();

    /// <summary>
    /// Well known assemblies, and well known namespaces inside those assemblies.
    /// </summary>
    public List<WellKnownAssembly> WellKnownAssemblies { get; } = new List<WellKnownAssembly>();

    /// <summary>
    /// An assembly to consider well known
    /// </summary>
    public class WellKnownAssembly
    {
      /// <summary>
      /// The assembly to consider well known.
      /// </summary>
      public Assembly Assembly { get; }

      /// <summary>
      /// The prefix namespaces types should be auto exported from.
      /// </summary>
      public string[] UsingNamespacePrefix { get; }

      /// <summary>
      /// Creates a new assembly record over the target assembly, with the predefined using statements applied.
      /// </summary>
      /// <param name="assembly">The assembly to use as well known.</param>
      /// <param name="usingNamespacePrefix">
      /// The namespaces to be considered having using declared.
      /// These should be suffixed with a . for namespace,
      /// or + for a class where children types are being exported.
      /// </param>
      public WellKnownAssembly(Assembly assembly, params string[] usingNamespacePrefix)
      {
        if (usingNamespacePrefix.Any(s => !(s.EndsWith('.') || s.EndsWith('+'))))
        {
          throw new ArgumentException("UsingNamespacePrefix entries must end with a . or +, as they are raw prefixes.", nameof(usingNamespacePrefix));
        }

        Assembly = assembly;
        UsingNamespacePrefix = usingNamespacePrefix;
      }
    }

    /// <summary>
    /// Attempts to parse the type name using TypeResolver (mostly like C#) format.
    /// </summary>
    /// <param name="typeName">The name of the type to resolve.</param>
    /// <returns>The resolved type, or null if not found.</returns>
    public Type? ParseType(string typeName)
    {
      try
      {
        return ParseInternal(typeName);
      }
      catch
      {
        // TODO: Log the error.
        return null;
      }
    }

    /// <summary>
    /// Attempts to parse the type name using TypeResolver (mostly like C#) format.
    /// </summary>
    /// <param name="typeName">The name of the type to resolve.</param>
    /// <returns>The resolved type, or null if not found.</returns>
    private Type? ParseInternal(string typeName)
    {
      typeName = typeName.Trim();

      // Type name is to be sent to the CLR type system.
      if (typeName.StartsWith('[') && typeName.EndsWith(']'))
      {
        var trimmedName = typeName.Substring(1, typeName.Length - 2);
        return Type.GetType(trimmedName);
      }

      // String represents a struct to be converted to a nullable type.
      if (typeName.EndsWith('?'))
      {
        var extractedType = ParseInternal(typeName.Remove(typeName.Length - 1));
        return extractedType != null && !extractedType.IsByRef
          ? typeof(Nullable<>).MakeGenericType(extractedType)
          : extractedType;
      }

      // Resolvers below this point fall through to the next if they fail, above this point stops resolution.

      return TryResolveWellKnown(typeName) ??
             TryResolve(typeName) ??
             ResolveGeneric(typeName);
    }

    /// <summary>
    /// Resolves a pre-defined well known type such as 'string' or 'int', mapping the string to the concrete type.
    /// </summary>
    /// <param name="shorthandTypeName">The short name of the type.</param>
    /// <returns>The hard type for the short name, if found.</returns>
    private Type? TryResolveWellKnown(string shorthandTypeName)
    {
      return WellKnownTypes.TryGetValue(shorthandTypeName, out var type) ? type : null;
    }

    /// <summary>
    /// Returns the filled out generic of the specific name and generic type count, or null.
    /// </summary>
    /// <param name="typeName">The TypeResolver formatted type string to resolve.</param>
    /// <returns>The filled out generic, or null if not found.</returns>
    private Type? ResolveGeneric(string typeName)
    {
      var match = TypeFormatRegex.Match(typeName);
      if (!match.Success)
      {
        return null;
      }

      var name = match.Groups["Name"].Value.Trim();

      var genStr = match.Groups["generics"].Value;
      List<Type> generics = new List<Type>();
      while (!string.IsNullOrWhiteSpace(genStr))
      {
        var current = ConsumeGenericDeclaration(ref genStr);
        var subType = ParseInternal(current);
        if (subType == null)
        {
          return null;
        }

        generics.Add(subType);
      }

      var candidate = TryResolve($"{name}`{generics.Count}");
      return candidate?.MakeGenericType(generics.ToArray());
    }

    /// <summary>
    /// A form of iterator that splits on commas, but only outside of scopes defined by [] and gators.
    /// </summary>
    /// <remarks>
    /// It is possible to cause very funky scopes by doing things like &gt;]int&lt;[,
    /// but those resolutions will fail from tokens not matching up later on so that is OK.
    /// The worst case is your type request succeeds even though your format was garbage,
    /// so you retrieved a type you already had access to retrieve if you parsed the type correctly.
    /// </remarks>
    /// <param name="genericStr">the generic string to read through for substrings, this is also used to return the remaining content to read.</param>
    /// <returns>A tuple of the current string, and the next string to iterate.</returns>
    private static string ConsumeGenericDeclaration(ref string genericStr)
    {
      var recurLevel = 0;
      for (var i = 0; i < genericStr.Length; i++)
      {
        var tok = genericStr[i];
        switch (tok)
        {
          case ',':
            if (recurLevel == 0)
            {
              var name = genericStr.Substring(0, i).Trim();
              genericStr = genericStr.Substring(i + 1);
              return name;
            }
            break;

          case '>':
          case ']':
            recurLevel--;
            break;

          case '<':
          case '[':
            recurLevel++;
            break;
        }
      }

      // wipe the newly consumed string.
      var cachedName = genericStr;
      genericStr = "";

      return cachedName;
    }

    /// <summary>
    /// Attempt to resolve the type name from the well known assembly list and namespaces.
    /// </summary>
    /// <param name="typeName">The name of the type to find.</param>
    /// <returns>The type if it was found, or null.</returns>
    private Type? TryResolve(string typeName)
    {
      Type? target = null;
      foreach (var knownAssembly in WellKnownAssemblies)
      {
        target = knownAssembly.Assembly.GetType(typeName);
        if (target != null)
        {
          return target;
        }

        foreach (var ns in knownAssembly.UsingNamespacePrefix)
        {
          target = knownAssembly.Assembly.GetType(ns + typeName);
          if (target != null)
          {
            return target;
          }
        }
      }

      return target;
    }
  }
}
