namespace JSNet.Syntax;

// ---------------------------------------------------------------------------------------------
// AST. Nodes are reference types (identity matters: the semantic model keys data by node).
// ---------------------------------------------------------------------------------------------

public abstract class Node
{
    public TextSpan Span { get; set; }
}

// ----------------------------------------- Types -------------------------------------------

public abstract class TypeNode : Node;

/// <summary><c>Name</c>, <c>A.B.C&lt;T1, T2&gt;</c>, optionally followed by <c>[]</c> ranks and <c>?</c>.</summary>
public sealed class NamedType(string name, List<TypeNode> typeArgs) : TypeNode
{
    public string Name { get; } = name;
    public List<TypeNode> TypeArgs { get; } = typeArgs;
}

public sealed class ArrayType(TypeNode element) : TypeNode
{
    public TypeNode Element { get; } = element;
}

public sealed class NullableType(TypeNode inner) : TypeNode
{
    public TypeNode Inner { get; } = inner;
}

public sealed class FunctionType(List<Parameter> parameters, TypeNode returnType) : TypeNode
{
    public List<Parameter> Parameters { get; } = parameters;
    public TypeNode ReturnType { get; } = returnType;
}

public sealed class TupleType(List<TypeNode> elements) : TypeNode
{
    public List<TypeNode> Elements { get; } = elements;
}

public sealed class UnionType(List<TypeNode> members) : TypeNode
{
    public List<TypeNode> Members { get; } = members;
}

/// <summary><c>{ name: string; age?: number }</c> — lowered to a generated record class per distinct shape.</summary>
public sealed class ObjectTypeNode(List<ClassMember> members) : TypeNode
{
    public List<ClassMember> Members { get; } = members;
}

// --------------------------------------- Common parts --------------------------------------

/// <summary><c>@Name(args)</c> decorator or <c>[Name(args)]</c> attribute; both lower to a CLR attribute.</summary>
public sealed class AttributeNode(string name, List<Expression> args) : Node
{
    public string Name { get; } = name;
    public List<Expression> Args { get; } = args;
    /// <summary>Optional attribute target such as <c>return</c> or <c>assembly</c>.</summary>
    public string? Target { get; init; }
}

[Flags]
public enum Modifiers
{
    None = 0,
    Public = 1 << 0,
    Private = 1 << 1,
    Protected = 1 << 2,
    Internal = 1 << 3,
    Static = 1 << 4,
    Readonly = 1 << 5,
    Abstract = 1 << 6,
    Override = 1 << 7,
    Virtual = 1 << 8,
    Sealed = 1 << 9,
    Partial = 1 << 10,
    Async = 1 << 11,
    Export = 1 << 12,
    Declare = 1 << 13,
    Accessibility = Public | Private | Protected | Internal,
}

public sealed class TypeParameter(string name, TypeNode? constraint) : Node
{
    public string Name { get; } = name;
    public TypeNode? Constraint { get; } = constraint;
}

public sealed class Parameter(string name) : Node
{
    public string Name { get; } = name;
    public Pattern? Pattern { get; init; }
    public TypeNode? Type { get; set; }
    public Expression? Default { get; init; }
    public bool Optional { get; init; }
    public bool Rest { get; init; }
    /// <summary>TypeScript-style constructor parameter property (<c>constructor(private repo: Repo)</c>).</summary>
    public Modifiers PropertyModifiers { get; init; }
    public List<AttributeNode> Attributes { get; init; } = [];
    /// <summary><c>out</c>, <c>ref</c> or <c>in</c> for by-reference CLR parameters.</summary>
    public string? RefKind { get; init; }
}

// -------------------------------------- Patterns -------------------------------------------

public abstract class Pattern : Node;

public sealed class IdentifierPattern(string name) : Pattern
{
    public string Name { get; } = name;
}

public sealed class ObjectPattern(List<(string Key, Pattern Value, Expression? Default)> properties) : Pattern
{
    public List<(string Key, Pattern Value, Expression? Default)> Properties { get; } = properties;
}

public sealed class ArrayPattern(List<Pattern?> elements) : Pattern
{
    public List<Pattern?> Elements { get; } = elements;
}

// ------------------------------------- Expressions -----------------------------------------

public abstract class Expression : Node;

public enum LiteralKind { Number, String, True, False, Null, Undefined }

public sealed class LiteralExpression(LiteralKind kind, string text, string? value = null) : Expression
{
    public LiteralKind Kind { get; } = kind;
    /// <summary>Raw source text.</summary>
    public string Text { get; } = text;
    /// <summary>Decoded value for strings.</summary>
    public string? Value { get; } = value;
}

public sealed class TemplateExpression(List<string> quasis, List<Expression> holes) : Expression
{
    /// <summary>Literal chunks (raw, still escaped). Count is always <c>Holes.Count + 1</c>.</summary>
    public List<string> Quasis { get; } = quasis;
    public List<Expression> Holes { get; } = holes;
}

public sealed class IdentifierExpression(string name) : Expression
{
    public string Name { get; } = name;
}

public sealed class ThisExpression : Expression;
public sealed class SuperExpression : Expression;

public sealed class ArrayExpression(List<Expression> elements) : Expression
{
    public List<Expression> Elements { get; } = elements;
}

public sealed class ObjectProperty(string key, Expression value, bool shorthand) : Node
{
    public string Key { get; } = key;
    public Expression Value { get; } = value;
    public bool Shorthand { get; } = shorthand;
    /// <summary><c>...value</c> inside an object literal.</summary>
    public bool IsSpread { get; init; }
}

/// <summary><c>/pattern/flags</c></summary>
public sealed class RegexExpression(string pattern, string flags) : Expression
{
    public string Pattern { get; } = pattern;
    public string Flags { get; } = flags;
}

/// <summary><c>class { ... }</c> used as a value; hoisted to a namespace-level class.</summary>
public sealed class ClassExpression(ClassDeclaration declaration) : Expression
{
    public ClassDeclaration Declaration { get; } = declaration;
}

/// <summary><c>out x</c>, <c>ref x</c>, <c>out let x</c> call arguments.</summary>
public sealed class RefArgumentExpression(string modifier, Expression? target, string? declaredName, TypeNode? declaredType) : Expression
{
    public string Modifier { get; } = modifier;
    public Expression? Target { get; } = target;
    public string? DeclaredName { get; } = declaredName;
    public TypeNode? DeclaredType { get; } = declaredType;
}

public sealed class ObjectExpression(List<ObjectProperty> properties) : Expression
{
    public List<ObjectProperty> Properties { get; } = properties;
}

public sealed class FunctionExpression(List<Parameter> parameters, Node body) : Expression
{
    public List<Parameter> Parameters { get; } = parameters;
    /// <summary>Either a <see cref="BlockStatement"/> or an <see cref="Expression"/> (concise arrow body).</summary>
    public Node Body { get; } = body;
    public TypeNode? ReturnType { get; init; }
    public bool IsArrow { get; init; }
    public bool IsAsync { get; init; }
    public string? Name { get; init; }
}

public sealed class UnaryExpression(string op, Expression operand, bool prefix = true) : Expression
{
    public string Operator { get; } = op;
    public Expression Operand { get; } = operand;
    public bool Prefix { get; } = prefix;
}

public sealed class AwaitExpression(Expression operand) : Expression
{
    public Expression Operand { get; } = operand;
}

public sealed class BinaryExpression(string op, Expression left, Expression right) : Expression
{
    public string Operator { get; } = op;
    public Expression Left { get; } = left;
    public Expression Right { get; } = right;
}

public sealed class AssignmentExpression(string op, Expression target, Expression value) : Expression
{
    public string Operator { get; } = op;
    public Expression Target { get; } = target;
    public Expression Value { get; } = value;
}

public sealed class ConditionalExpression(Expression test, Expression whenTrue, Expression whenFalse) : Expression
{
    public Expression Test { get; } = test;
    public Expression WhenTrue { get; } = whenTrue;
    public Expression WhenFalse { get; } = whenFalse;
}

public sealed class CallExpression(Expression callee, List<TypeNode> typeArgs, List<Expression> args, bool optional) : Expression
{
    public Expression Callee { get; } = callee;
    public List<TypeNode> TypeArgs { get; } = typeArgs;
    public List<Expression> Args { get; } = args;
    public bool Optional { get; } = optional;
}

public sealed class NewExpression(TypeNode type, List<Expression> args) : Expression
{
    public TypeNode Type { get; } = type;
    public List<Expression> Args { get; } = args;
    public ObjectExpression? Initializer { get; init; }
    /// <summary><c>new T[size]</c>: <see cref="Args"/> holds the single size expression.</summary>
    public bool IsArrayCreation { get; init; }
}

public sealed class MemberExpression(Expression target, string name, bool optional) : Expression
{
    public Expression Target { get; } = target;
    public string Name { get; } = name;
    public bool Optional { get; } = optional;
}

public sealed class IndexExpression(Expression target, Expression index, bool optional) : Expression
{
    public Expression Target { get; } = target;
    public Expression Index { get; } = index;
    public bool Optional { get; } = optional;
}

public sealed class AsExpression(Expression operand, TypeNode type) : Expression
{
    public Expression Operand { get; } = operand;
    public TypeNode Type { get; } = type;
}

public sealed class IsTypeExpression(Expression operand, TypeNode type) : Expression
{
    public Expression Operand { get; } = operand;
    public TypeNode Type { get; } = type;
}

public sealed class NonNullExpression(Expression operand) : Expression
{
    public Expression Operand { get; } = operand;
}

public sealed class SpreadExpression(Expression operand) : Expression
{
    public Expression Operand { get; } = operand;
}

public sealed class ParenthesizedExpression(Expression inner) : Expression
{
    public Expression Inner { get; } = inner;
}

/// <summary><c>typeof T</c> used with a type name in a CLR context, e.g. <c>nameof</c>-like reflection helpers.</summary>
public sealed class TypeOfTypeExpression(TypeNode type) : Expression
{
    public TypeNode Type { get; } = type;
}

// -------------------------------------- Statements -----------------------------------------

public abstract class Statement : Node
{
    public List<AttributeNode> Attributes { get; set; } = [];
}

public sealed class BlockStatement(List<Statement> statements) : Statement
{
    public List<Statement> Statements { get; } = statements;
}

public sealed class EmptyStatement : Statement;

public sealed class ExpressionStatement(Expression expression) : Statement
{
    public Expression Expression { get; } = expression;
}

public enum VariableKind { Let, Const, Var, Dynamic }

public sealed class VariableDeclarator(Pattern target, TypeNode? type, Expression? init) : Node
{
    public Pattern Target { get; } = target;
    public TypeNode? Type { get; } = type;
    public Expression? Init { get; } = init;
}

public sealed class VariableStatement(VariableKind kind, List<VariableDeclarator> declarators) : Statement
{
    public VariableKind Kind { get; } = kind;
    public List<VariableDeclarator> Declarators { get; } = declarators;
    public Modifiers Modifiers { get; set; }
}

public sealed class IfStatement(Expression test, Statement then, Statement? @else) : Statement
{
    public Expression Test { get; } = test;
    public Statement Then { get; } = then;
    public Statement? Else { get; } = @else;
}

public sealed class ForStatement(Node? init, Expression? test, Expression? update, Statement body) : Statement
{
    /// <summary><see cref="VariableStatement"/> or <see cref="Expression"/>.</summary>
    public Node? Init { get; } = init;
    public Expression? Test { get; } = test;
    public Expression? Update { get; } = update;
    public Statement Body { get; } = body;
}

public sealed class ForOfStatement(VariableKind kind, Pattern target, TypeNode? type, Expression iterable, Statement body, bool isIn) : Statement
{
    public VariableKind Kind { get; } = kind;
    public Pattern Target { get; } = target;
    public TypeNode? Type { get; } = type;
    public Expression Iterable { get; } = iterable;
    public Statement Body { get; } = body;
    /// <summary>True for <c>for (k in obj)</c>: iterates keys.</summary>
    public bool IsIn { get; } = isIn;
    public bool IsAwait { get; init; }
}

public sealed class WhileStatement(Expression test, Statement body) : Statement
{
    public Expression Test { get; } = test;
    public Statement Body { get; } = body;
}

public sealed class DoWhileStatement(Statement body, Expression test) : Statement
{
    public Statement Body { get; } = body;
    public Expression Test { get; } = test;
}

public sealed class ReturnStatement(Expression? value) : Statement
{
    public Expression? Value { get; } = value;
}

public sealed class YieldStatement(Expression? value, bool isBreak) : Statement
{
    public Expression? Value { get; } = value;
    public bool IsBreak { get; } = isBreak;
}

public sealed class BreakStatement : Statement
{
    public string? Label { get; init; }
}

public sealed class ContinueStatement : Statement
{
    public string? Label { get; init; }
}

public sealed class LabeledStatement(string label, Statement body) : Statement
{
    public string Label { get; } = label;
    public Statement Body { get; } = body;
}

public sealed class ThrowStatement(Expression value) : Statement
{
    public Expression Value { get; } = value;
}

public sealed class CatchClause(string? name, TypeNode? type, BlockStatement body) : Node
{
    public string? Name { get; } = name;
    public TypeNode? Type { get; } = type;
    public BlockStatement Body { get; } = body;
}

public sealed class TryStatement(BlockStatement block, List<CatchClause> catches, BlockStatement? @finally) : Statement
{
    public BlockStatement Block { get; } = block;
    public List<CatchClause> Catches { get; } = catches;
    public BlockStatement? Finally { get; } = @finally;
}

public sealed class SwitchCase(Expression? test, List<Statement> body) : Node
{
    /// <summary>Null for <c>default</c>.</summary>
    public Expression? Test { get; } = test;
    public List<Statement> Body { get; } = body;
}

public sealed class SwitchStatement(Expression discriminant, List<SwitchCase> cases) : Statement
{
    public Expression Discriminant { get; } = discriminant;
    public List<SwitchCase> Cases { get; } = cases;
}

/// <summary><c>using (const x = ...) { }</c> / <c>using x = ...;</c> → deterministic disposal.</summary>
public sealed class UsingStatement(VariableStatement declaration, Statement? body) : Statement
{
    public VariableStatement Declaration { get; } = declaration;
    public Statement? Body { get; } = body;
}

// ------------------------------------- Declarations ----------------------------------------

public sealed class FunctionDeclaration(string name, List<Parameter> parameters, BlockStatement? body) : Statement
{
    public string Name { get; } = name;
    public List<Parameter> Parameters { get; } = parameters;
    public BlockStatement? Body { get; } = body;
    public List<TypeParameter> TypeParameters { get; init; } = [];
    public TypeNode? ReturnType { get; init; }
    public Modifiers Modifiers { get; set; }
    public bool IsGenerator { get; init; }
    /// <summary><c>ai function</c>: body (if any) is a prompt template; the call goes to <c>JSNet.AI</c>.</summary>
    public bool IsAi { get; init; }
    public bool IsAsync => (Modifiers & Modifiers.Async) != 0;
}

public enum MemberKind { Field, Method, Getter, Setter, Constructor }

public sealed class ClassMember(MemberKind kind, string name) : Node
{
    public MemberKind Kind { get; } = kind;
    public string Name { get; } = name;
    public Modifiers Modifiers { get; set; }
    public TypeNode? Type { get; set; }
    public Expression? Init { get; init; }
    public bool Optional { get; init; }
    public List<Parameter> Parameters { get; init; } = [];
    public List<TypeParameter> TypeParameters { get; init; } = [];
    public BlockStatement? Body { get; init; }
    public bool IsGenerator { get; init; }
    public List<AttributeNode> Attributes { get; init; } = [];
    public bool IsStatic => (Modifiers & Modifiers.Static) != 0;
    public bool IsAsync => (Modifiers & Modifiers.Async) != 0;
}

public enum ClassKind { Class, Struct, Record }

public sealed class ClassDeclaration(string name, List<ClassMember> members) : Statement
{
    /// <summary>Settable so anonymous class expressions can take the name of the variable they initialize.</summary>
    public string Name { get; set; } = name;
    public List<ClassMember> Members { get; } = members;
    public ClassKind Kind { get; init; }
    public List<TypeParameter> TypeParameters { get; init; } = [];
    public TypeNode? Extends { get; init; }
    public List<TypeNode> Implements { get; init; } = [];
    public Modifiers Modifiers { get; set; }
}

public sealed class InterfaceDeclaration(string name, List<ClassMember> members) : Statement
{
    public string Name { get; } = name;
    public List<ClassMember> Members { get; } = members;
    public List<TypeParameter> TypeParameters { get; init; } = [];
    public List<TypeNode> Extends { get; init; } = [];
    public Modifiers Modifiers { get; set; }
}

public sealed class EnumMember(string name, Expression? value) : Node
{
    public string Name { get; } = name;
    public Expression? Value { get; } = value;
}

public sealed class EnumDeclaration(string name, List<EnumMember> members) : Statement
{
    public string Name { get; } = name;
    public List<EnumMember> Members { get; } = members;
    public Modifiers Modifiers { get; set; }
}

public sealed class TypeAliasDeclaration(string name, TypeNode type) : Statement
{
    public string Name { get; } = name;
    public TypeNode Type { get; } = type;
}

public enum ImportKind
{
    /// <summary><c>import System.Text;</c> → <c>using System.Text;</c></summary>
    Namespace,
    /// <summary><c>import { A, B as C } from "Ns";</c> → type aliases.</summary>
    Named,
    /// <summary><c>import * as IO from "System.IO";</c> → namespace alias.</summary>
    Alias,
    /// <summary><c>import static System.Math;</c> → <c>using static</c>.</summary>
    Static,
    /// <summary><c>import { x } from "./file";</c> → no-op (same assembly, globally visible).</summary>
    Module,
}

public sealed class ImportDeclaration(ImportKind kind, string module) : Statement
{
    public ImportKind Kind { get; } = kind;
    public string Module { get; } = module;
    public List<(string Name, string Alias)> Names { get; init; } = [];
    public string? Alias { get; init; }
}

public sealed class NamespaceDeclaration(string name) : Statement
{
    public string Name { get; } = name;
}

/// <summary>
/// <c>prompt Name { input: string; output: T; template: `...${input}...`; }</c>
/// → a reusable, strongly typed prompt class backed by <c>JSNet.AI</c>.
/// </summary>
public sealed class PromptDeclaration(string name, List<ClassMember> fields, Expression? template, TypeNode? output) : Statement
{
    public string Name { get; } = name;
    public List<ClassMember> Fields { get; } = fields;
    public Expression? Template { get; } = template;
    public TypeNode? Output { get; } = output;
    public Modifiers Modifiers { get; set; }
}

public sealed class CompilationUnit(SourceText source, List<Statement> statements) : Node
{
    public SourceText Source { get; } = source;
    public List<Statement> Statements { get; } = statements;
    /// <summary>Every object type literal in the file (the compiler generates one record per distinct shape).</summary>
    public List<ObjectTypeNode> ObjectTypes { get; init; } = [];
}
