namespace GodotResourceGenerator.Tests;

public sealed class GodotResourceGeneratorTests
{
    [Fact]
    public void Generates_resource_with_exports_and_model_copy_helpers()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public enum UserKind
            {
                Regular,
            }

            public class User
            {
                public int Id { get; set; }
                public string Name { get; set; }
                public UserKind Kind { get; set; }
                public Godot.Vector3 Position { get; set; }
            }

            [GodotResource(typeof(User))]
            internal partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = Assert.Single(result.GeneratedResourceSources).Source;

        Assert.Contains("namespace Demo", generated);
        Assert.Contains("[global::Godot.GlobalClass]", generated);
        Assert.Contains("internal partial class UserResource : global::Godot.Resource", generated);
        Assert.Contains("[global::Godot.Export]", generated);
        Assert.Contains("public int Id { get; set; }", generated);
        Assert.Contains("public string Name { get; set; }", generated);
        Assert.Contains("public global::Demo.UserKind Kind { get; set; }", generated);
        Assert.Contains("public global::Godot.Vector3 Position { get; set; }", generated);
        Assert.Contains("public static global::Demo.UserResource FromModel(global::Demo.User model)", generated);
        Assert.Contains("public void CopyFrom(global::Demo.User model)", generated);
        Assert.Contains("Id = model.Id;", generated);
        Assert.Contains("Name = model.Name;", generated);
    }

    [Fact]
    public void Maps_nested_annotated_models_to_generated_resources()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class Address
            {
                public string City { get; set; }
            }

            public class User
            {
                public Address Home { get; set; }
            }

            [GodotResource(typeof(Address))]
            public partial class AddressResource;

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = result.GeneratedResourceSources.Single(source => source.HintName == "Demo.UserResource.GodotResource.g.cs").Source;

        Assert.Contains("public global::Demo.AddressResource Home { get; set; }", generated);
        Assert.Contains("Home = model.Home is null ? null : global::Demo.AddressResource.FromModel(model.Home);", generated);
    }

    [Fact]
    public void Converts_arrays_and_clr_sequences_with_mapped_elements()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class Address
            {
                public string City { get; set; }
            }

            public class User
            {
                public Address[] AddressArray { get; set; }
                public System.Collections.Generic.List<Address> Addresses { get; set; }
            }

            [GodotResource(typeof(Address))]
            public partial class AddressResource;

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = result.GeneratedResourceSources.Single(source => source.HintName == "Demo.UserResource.GodotResource.g.cs").Source;

        Assert.Contains("public global::Demo.AddressResource[] AddressArray { get; set; }", generated);
        Assert.Contains("var __AddressArray = new global::Demo.AddressResource[model.AddressArray.Length];", generated);
        Assert.Contains("__AddressArray[__AddressArrayIndex] = model.AddressArray[__AddressArrayIndex] is null ? null : global::Demo.AddressResource.FromModel(model.AddressArray[__AddressArrayIndex]);", generated);
        Assert.Contains("public global::Godot.Collections.Array<global::Demo.AddressResource> Addresses { get; set; }", generated);
        Assert.Contains("var __Addresses = new global::Godot.Collections.Array<global::Demo.AddressResource>();", generated);
        Assert.Contains("__Addresses.Add(__AddressesItem is null ? null : global::Demo.AddressResource.FromModel(__AddressesItem));", generated);
    }

    [Fact]
    public void Preserves_public_resource_accessibility()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public string Name { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = Assert.Single(result.GeneratedResourceSources).Source;

        Assert.Contains("public partial class UserResource : global::Godot.Resource", generated);
    }

    [Fact]
    public void Reports_invalid_resource_target()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public string Name { get; set; }
            }

            [GodotResource(typeof(User))]
            internal class UserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0001");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Reports_duplicate_model_mapping()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public string Name { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class FirstUserResource;

            [GodotResource(typeof(User))]
            public partial class SecondUserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0003");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Reports_missing_godot_reference()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public string Name { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """, includeGodot: false);

        result.SingleGeneratorDiagnostic("GRG0005");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Reports_unsupported_property_type()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public decimal Balance { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0006");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Reports_nullable_value_type()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public int? Age { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0006");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Reports_unsupported_regular_dictionary()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public System.Collections.Generic.Dictionary<string, int> Stats { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0007");
        Assert.Empty(result.GeneratedResourceSources);
    }
}
