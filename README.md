# Godot Resource Generator

[![NuGet](https://img.shields.io/nuget/v/GodotResourceGenerator.svg)](https://www.nuget.org/packages/GodotResourceGenerator)
[![NuGet (pre-release)](https://img.shields.io/nuget/vpre/GodotResourceGenerator.svg)](https://www.nuget.org/packages/GodotResourceGenerator)
[![License: MIT](https://img.shields.io/github/license/sadicangel/godot-resource-generator)](LICENSE)
[![Build](https://img.shields.io/github/actions/workflow/status/sadicangel/godot-resource-generator/build.yml?label=build)](https://github.com/sadicangel/godot-resource-generator/actions)

**Godot Resource Generator** is a .NET source generator that creates Godot `Resource` classes from existing C# model types.

It is designed for projects that keep regular domain models separate from Godot editor-facing resources, while still wanting strongly typed exported properties and simple model-to-resource mapping.

## Prerequisites

- .NET SDK 8.0 or later for consuming projects.
- A Godot C# project that references Godot 4 C# assemblies.

## Installation

Install the package:

```bash
dotnet add package GodotResourceGenerator --version 0.1.0
```

Mark it as a build-only dependency in your `.csproj` file:

```xml
<PackageReference Include="GodotResourceGenerator"
                  Version="0.1.0"
                  PrivateAssets="all"
                  ExcludeAssets="runtime" />
```

## Basic Usage

Create a regular model type:

```csharp
public sealed class User
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public Godot.Vector3 SpawnPosition { get; set; }
}
```

Add a partial resource class and point it at the model:

```csharp
using GodotResourceGenerator;

[GodotResource(typeof(User))]
internal partial class UserResource;
```

The generator emits a Godot resource:

```csharp
[global::Godot.GlobalClass]
internal partial class UserResource : global::Godot.Resource
{
    [global::Godot.Export]
    public string Name { get; set; }

    [global::Godot.Export]
    public int Level { get; set; }

    [global::Godot.Export]
    public global::Godot.Vector3 SpawnPosition { get; set; }

    public static UserResource FromModel(User model) { ... }

    public void CopyFrom(User model) { ... }
}
```

## Custom Resource Bases

Generated resources derive from `Godot.Resource` by default. To use your own base resource type, either pass it as the second attribute argument:

```csharp
public abstract class GameResource : Godot.Resource
{
}

[GodotResource(typeof(User), typeof(GameResource))]
internal partial class UserResource;
```

Or declare the base on the partial resource class:

```csharp
[GodotResource(typeof(User))]
internal partial class UserResource : GameResource;
```

The custom base type must derive from `Godot.Resource`. If both forms are used, they must name the same base type.

## Supported Types

The generator supports public readable instance properties whose types can be exported by Godot:

- C# primitive Variant-compatible types and `string`.
- Enums.
- Nullable value types when the underlying type is supported, such as `int?` or `MyEnum?`.
- Godot built-in structs such as `Vector2`, `Vector3`, and `Color`.
- Types deriving from `Godot.GodotObject`.
- `Godot.Collections.Array`, `Array<T>`, `Dictionary`, and `Dictionary<TKey, TValue>` when generic arguments are supported.
- C# arrays.
- CLR sequences such as `List<T>` and `IReadOnlyList<T>`, emitted as `Godot.Collections.Array<T>`.
- Nested model classes or structs annotated with `[GodotResource]`, emitted as their generated resource type.
- Derived model properties can use the nearest mapped base model resource.

Unsupported property types produce diagnostics and suppress generation for the affected resource.

## Mapping Helpers

Generated resources include:

- `static FromModel(TModel model)` to create a new resource.
- `CopyFrom(TModel model)` to update an existing resource.

Nested annotated models are mapped recursively. CLR sequences are copied into new Godot arrays.

## License

Licensed under the [MIT License](LICENSE).
