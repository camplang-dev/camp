using Xunit;

namespace Camp.Compiler.Tests;

public sealed class EscapedTypeLifetimeTests
{
	const string Declarations = """
		extern escaped void* malloc(nuint size);
		escaped class Widget { int value; }
		escaped interface Sink { void put(int value); }
		struct StackSink: Sink { void put(int value): Sink { } }
		class ExplicitSink: Sink { void put(escaped this, int value): Sink { } }
		escaped class DefaultSink: Sink { void put(int value): Sink { } }
		Widget* kept;
		Sink* keptSink;
		StackSink globalSink;
		void keep(escaped Widget* widget) { kept = widget; }
		""";

	[Theory]
	[InlineData("void use(Widget* widget) { kept = widget; keep(widget); }")]
	[InlineData("void use(Sink* sink) { keptSink = sink; }")]
	[InlineData("Widget* use(Widget* widget) { auto copy = widget; return copy; }")]
	[InlineData("struct Box { Widget* value; void set(Widget* value) { this.value = value; } }")]
	[InlineData("void use() { Sink* view = globalSink; keptSink = view; }")]
	[InlineData("void use() { StackSink* source = new StackSink(); Sink* view = source; keptSink = view; }")]
	[InlineData("void use() { ExplicitSink* source = new ExplicitSink(); Sink* view = source; view.put(1); }")]
	[InlineData("void use(DefaultSink* source) { Sink* view = source; view.put(1); }")]
	[InlineData("void use(escaped Widget* widget) { kept = widget; }")]
	[InlineData("void use(Widget* widget) { escaped Widget* copy = widget; keep(copy); }")]
	[InlineData("T* echo<T>(T* value) { return value; } void use(Widget* value) { keep(echo<Widget>(value)); }")]
	[InlineData("escaped interface Allocator { } struct Arena: Allocator { } Arena arena; void borrow(within allocator) { } void use() { within (arena) borrow(); }")]
	[InlineData("escaped interface Allocator { } struct Arena: Allocator { } void borrow(within allocator) { } void use() { auto arena = new Arena(); within (arena) borrow(); }")]
	public void Escaped_pointer_types_supply_escaped_contracts(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(Declarations + source));
	}

	[Theory]
	[InlineData("void use() { Widget local = default; }")]
	[InlineData("void use() { Widget* local = stackalloc Widget(); }")]
	[InlineData("void use() { fixed Widget[2] local = default; }")]
	[InlineData("void use() { StackSink local = default; Sink* view = local; }")]
	[InlineData("void use(StackSink* local) { Sink* view = local; }")]
	[InlineData("void use(ExplicitSink* local) { Sink* view = local; }")]
	[InlineData("void use() { ExplicitSink local = default; Sink* view = &local; }")]
	[InlineData("void use() { ExplicitSink* local = stackalloc ExplicitSink(); Sink* view = local; }")]
	[InlineData("void accept(Sink* view) { } void use() { StackSink local = default; accept(local); }")]
	[InlineData("Sink* use() { StackSink local = default; return local; }")]
	[InlineData("void use(scoped Widget* widget) { }")]
	[InlineData("void use(int* owner, unscoped(owner) Widget* widget) { }")]
	[InlineData("escaped interface Allocator { } void use(within scoped allocator) { }")]
	[InlineData("escaped interface Allocator { } struct Arena: Allocator { } void borrow(within allocator) { } void use() { Arena local = default; within (local) borrow(); }")]
	public void Escaped_types_reject_frame_storage_and_shorter_contracts(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(Declarations + source);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("escaped type") || diagnostic.Message.Contains("escaped interface"));
	}

	[Fact]
	public void Escaped_interface_accessor_requires_an_escaped_receiver()
	{
		var compilation = SemanticCompiler.CompileLowered(Declarations);
		SemanticCompiler.AssertNoDiagnostics(compilation);
		var owner = Assert.Single(SemanticCompiler.Descendants<ClassDefinition>(compilation.Module), type => type.Name == "ExplicitSink");
		var accessor = Assert.Single(owner.Functions, method => method.Name == "getSink");
		Assert.StartsWith("escaped", accessor.ReceiverLifetimeBinding);
		Assert.StartsWith("escaped", accessor.EffectiveThisParameter!.LifetimeBinding);
	}

	[Theory]
	[InlineData("virtual escaped class Root { } sealed class Derived: Root { }")]
	[InlineData("virtual class Root { } sealed escaped class Derived: Root { }")]
	[InlineData("escaped interface Root { void run(); } interface Derived: Root { }")]
	[InlineData("escaped class Item { } struct Holder { Item item; }")]
	[InlineData("escaped class Item { } struct Holder { fixed Item[2] items; }")]
	public void Escaped_hierarchies_and_by_value_struct_fields_are_checked(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(source);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("escaped"));
	}

	[Theory]
	[InlineData("virtual escaped class Root { } sealed escaped class Derived: Root { }")]
	[InlineData("interface Plain { void stop(); } escaped interface Job: Plain { void run(); }")]
	[InlineData("escaped class Item { } struct Holder { Item* item; static Item storage; }")]
	public void Valid_escaped_hierarchies_and_pointer_fields_remain_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(source));
	}

	[Theory]
	[InlineData("Widget** keptSlot; void use(Widget* value) { keptSlot = &value; }")]
	[InlineData("Widget** keptSlot; void use(Widget* value) { Widget* copy = value; keptSlot = &copy; }")]
	public void An_escaped_pointer_value_does_not_make_its_frame_slot_escaped(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(Declarations + source);
		Assert.Contains(compilation.AnalysisDiagnostics, diagnostic => diagnostic.Message.Contains("non-escaped pointer-bearing value"));
	}

	[Theory]
	[InlineData("escaped interface Sink { void put(int value); } Sink kept; void store(Sink value) { kept = value; }")]
	[InlineData("escaped interface Sink { void put(int value); } void keep(escaped Sink value) { } void pass(Sink value) { keep(value); }")]
	[InlineData("escaped class Widget { } escaped class Panel { Widget widget; }")]
	[InlineData("escaped interface Sink { void put(int value); } void use() { Sink value = default; }")]
	[InlineData("escaped interface Sink { void put(int value); } struct Holder { Sink value; } class Box { Sink value; }")]
	public void Escaped_vtable_values_and_escaped_class_fields_are_valid(string source)
	{
		SemanticCompiler.AssertNoDiagnostics(SemanticCompiler.CompileLowered(source));
	}

	[Theory]
	[InlineData("escaped class Widget { } class Box { Widget widget; }")]
	[InlineData("escaped class Widget { } class Box { fixed Widget[2] widgets; }")]
	[InlineData("interface Sink { void put(int value); } Sink kept; void store(Sink value) { kept = value; }")]
	[InlineData("escaped interface Sink { void put(int value); } void use(Sink value) { Sink* pointer = &value; }")]
	[InlineData("escaped interface Sink { void put(int value); } void use() { Sink value = default; Sink* pointer = &value; }")]
	[InlineData("escaped interface Sink { void put(int value); } void use(scoped Sink value) { }")]
	public void Frame_owned_class_fields_and_ordinary_interface_values_remain_restricted(string source)
	{
		var compilation = SemanticCompiler.CompileLowered(source);
		Assert.NotEmpty(compilation.AnalysisDiagnostics);
	}
}
