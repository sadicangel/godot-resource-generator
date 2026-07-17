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
    public void Attribute_base_type_changes_generated_resource_base()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class User
            {
                public string Name { get; set; }
            }

            public class InventoryResourceBase : Godot.Resource
            {
            }

            [GodotResource(typeof(User), typeof(InventoryResourceBase))]
            public partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = Assert.Single(result.GeneratedResourceSources).Source;

        Assert.Contains("public partial class UserResource : global::Demo.InventoryResourceBase", generated);
    }

    [Fact]
    public void Explicit_resource_base_type_is_honored_without_duplicate_base_clause()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class User
            {
                public string Name { get; set; }
            }

            public class InventoryResourceBase : Godot.Resource
            {
            }

            [GodotResource(typeof(User))]
            public partial class UserResource : InventoryResourceBase;
            """);

        result.AssertNoErrors();
        var generated = Assert.Single(result.GeneratedResourceSources).Source;

        Assert.Contains("public partial class UserResource", generated);
        Assert.DoesNotContain("public partial class UserResource :", generated);
    }

    [Fact]
    public void Does_not_redeclare_properties_supplied_by_handwritten_resource_base()
    {
        var result = GeneratorTestDriver.Run("""
            using Godot;
            using GodotResourceGenerator;

            namespace Demo;

            public enum Seat
            {
                Red,
                Blue,
            }

            public abstract record GameEvent(string? ClientRequestId);

            public sealed record MatchEndedEvent(
                Seat? Winner,
                string? ClientRequestId)
                : GameEvent(ClientRequestId);

            public abstract partial class GameEventResource : Resource
            {
                [Export] public string? ClientRequestId { get; set; }
            }

            [GodotResource(typeof(MatchEndedEvent))]
            public partial class MatchEndedEventResource : GameEventResource;
            """);

        result.AssertNoErrors();
        var generated = Assert.Single(result.GeneratedResourceSources).Source;

        Assert.Contains("public global::Demo.Seat? Winner { get; set; }", generated);
        Assert.DoesNotContain("ClientRequestId { get; set; }", generated);
        Assert.Contains("ClientRequestId = model.ClientRequestId;", generated);
    }

    [Fact]
    public void Calls_generated_base_copy_for_properties_supplied_by_generated_resource_base()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public enum Seat
            {
                Red,
                Blue,
            }

            public abstract record GameEvent(string? ClientRequestId);

            public sealed record MatchEndedEvent(
                Seat? Winner,
                string? ClientRequestId)
                : GameEvent(ClientRequestId);

            [GodotResource(typeof(GameEvent))]
            public partial class GameEventResource;

            [GodotResource(typeof(MatchEndedEvent))]
            public partial class MatchEndedEventResource : GameEventResource;
            """);

        result.AssertNoErrors();
        var generated = result.GeneratedResourceSources.Single(source => source.HintName == "Demo.MatchEndedEventResource.GodotResource.g.cs").Source;

        Assert.Contains("public global::Demo.Seat? Winner { get; set; }", generated);
        Assert.DoesNotContain("ClientRequestId { get; set; }", generated);
        Assert.Contains("base.CopyFrom(model);", generated);
        Assert.DoesNotContain("ClientRequestId = model.ClientRequestId;", generated);
    }

    [Fact]
    public void Reports_mismatched_attribute_and_explicit_resource_base_type()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class User
            {
                public string Name { get; set; }
            }

            public class FirstResourceBase : Godot.Resource
            {
            }

            public class SecondResourceBase : Godot.Resource
            {
            }

            [GodotResource(typeof(User), typeof(FirstResourceBase))]
            public partial class UserResource : SecondResourceBase;
            """);

        result.SingleGeneratorDiagnostic("GRG0008");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Reports_non_resource_attribute_base_type()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class User
            {
                public string Name { get; set; }
            }

            public class NotAResource
            {
            }

            [GodotResource(typeof(User), typeof(NotAResource))]
            public partial class UserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0008");
        Assert.Empty(result.GeneratedResourceSources);
    }

    [Fact]
    public void Interface_only_partial_resource_declarations_still_default_to_godot_resource()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class User
            {
                public string Name { get; set; }
            }

            public interface IUserResource
            {
            }

            [GodotResource(typeof(User))]
            public partial class UserResource : IUserResource;
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
    public void Allows_unresolved_property_types_without_generator_diagnostic()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public MissingType Missing { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Single(result.GeneratedResourceSources);
        Assert.Contains(result.CompilationErrors, diagnostic => diagnostic.Id == "CS0246");
    }

    [Fact]
    public void Generates_nullable_export_when_underlying_type_is_directly_supported()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public enum Seat
            {
                Red,
                Blue,
            }

            public class User
            {
                public Seat? Seat { get; set; }
            }

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = Assert.Single(result.GeneratedResourceSources).Source;

        Assert.Contains("public global::Demo.Seat? Seat { get; set; }", generated);
        Assert.Contains("Seat = model.Seat;", generated);
    }

    [Fact]
    public void Reports_nullable_value_type_when_underlying_type_is_unsupported()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            public class User
            {
                public decimal? Balance { get; set; }
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

    [Fact]
    public void Maps_derived_model_properties_to_nearest_mapped_base_resource()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class Animal
            {
                public string Name { get; set; }
            }

            public class Dog : Animal
            {
            }

            public class User
            {
                public Dog Pet { get; set; }
            }

            [GodotResource(typeof(Animal))]
            public partial class AnimalResource;

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.AssertNoErrors();
        var generated = result.GeneratedResourceSources.Single(source => source.HintName == "Demo.UserResource.GodotResource.g.cs").Source;

        Assert.Contains("public global::Demo.AnimalResource Pet { get; set; }", generated);
        Assert.Contains("Pet = model.Pet is null ? null : global::Demo.AnimalResource.FromModel(model.Pet);", generated);
    }

    [Fact]
    public void Does_not_map_base_model_property_to_derived_only_resource()
    {
        var result = GeneratorTestDriver.Run("""
            using GodotResourceGenerator;

            namespace Demo;

            public class Animal
            {
            }

            public class Dog : Animal
            {
            }

            public class User
            {
                public Animal Pet { get; set; }
            }

            [GodotResource(typeof(Dog))]
            public partial class DogResource;

            [GodotResource(typeof(User))]
            public partial class UserResource;
            """);

        result.SingleGeneratorDiagnostic("GRG0006");
        Assert.Contains(result.GeneratedResourceSources, source => source.HintName == "Demo.DogResource.GodotResource.g.cs");
        Assert.DoesNotContain(result.GeneratedResourceSources, source => source.HintName == "Demo.UserResource.GodotResource.g.cs");
    }
}
