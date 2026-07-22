using System;
using System.Collections.Generic;
using System.Linq;
using Esprima;
using Esprima.Ast;

namespace CodexPortableManager
{
    // 解析器仅用于定位源码区间；兼容变换始终保留目标区间之外的官方字节。
    internal sealed class JavaScriptSemanticDocument
    {
        private readonly List<JavaScriptNodeRecord> records;
        private readonly Dictionary<Node, JavaScriptNodeRecord> recordsByNode;

        private JavaScriptSemanticDocument(string source, Node root)
        {
            Source = source;
            Root = root;
            records = new List<JavaScriptNodeRecord>();
            recordsByNode = new Dictionary<Node, JavaScriptNodeRecord>();
            Index(root, null);
        }

        internal string Source { get; private set; }
        internal Node Root { get; private set; }

        internal IEnumerable<JavaScriptNodeRecord> Records
        {
            get { return records; }
        }

        internal static JavaScriptSemanticDocument Parse(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            JavaScriptParser parser = new JavaScriptParser(new ParserOptions
            {
                Tolerant = false,
                RegExpParseMode = RegExpParseMode.Skip,
                MaxAssignmentDepth = 1024
            });
            try
            {
                return new JavaScriptSemanticDocument(source, parser.ParseScript(source));
            }
            catch (ParserException scriptException)
            {
                try
                {
                    parser = new JavaScriptParser(new ParserOptions
                    {
                        Tolerant = false,
                        RegExpParseMode = RegExpParseMode.Skip,
                        MaxAssignmentDepth = 1024
                    });
                    return new JavaScriptSemanticDocument(source, parser.ParseModule(source));
                }
                catch (ParserException moduleException)
                {
                    throw new InvalidOperationException(
                        "JavaScript 语义解析失败：script=" +
                        FormatParserException(scriptException, source) +
                        "；module=" + FormatParserException(moduleException, source),
                        moduleException);
                }
            }
        }

        private static string FormatParserException(
            ParserException exception,
            string source)
        {
            if (exception == null) return "未知解析错误";
            int index = exception.Index;
            int start = Math.Max(0, index - 80);
            int length = source == null
                ? 0
                : Math.Min(180, source.Length - start);
            string context = length <= 0
                ? string.Empty
                : source.Substring(start, length)
                    .Replace("\r", " ")
                    .Replace("\n", " ");
            return exception.Description +
                "（行=" + exception.LineNumber +
                "，列=" + exception.Column +
                "，索引=" + index +
                (context.Length == 0 ? string.Empty : "，上下文=" + context) +
                "）";
        }

        internal JavaScriptNodeRecord RecordFor(Node node)
        {
            if (node == null) return null;
            JavaScriptNodeRecord record;
            return recordsByNode.TryGetValue(node, out record) ? record : null;
        }

        internal string Slice(Node node)
        {
            if (node == null || node.Range.Start < 0 || node.Range.End < node.Range.Start ||
                node.Range.End > Source.Length)
            {
                throw new InvalidOperationException("JavaScript AST 节点源码区间无效。");
            }
            return Source.Substring(node.Range.Start, node.Range.End - node.Range.Start);
        }

        internal IEnumerable<JavaScriptNodeRecord> Descendants(Node ancestor)
        {
            if (ancestor == null) return Enumerable.Empty<JavaScriptNodeRecord>();
            JavaScriptNodeRecord record = RecordFor(ancestor);
            if (record == null) return Enumerable.Empty<JavaScriptNodeRecord>();
            int count = record.SubtreeEndIndex - record.Index - 1;
            return count <= 0
                ? Enumerable.Empty<JavaScriptNodeRecord>()
                : records.GetRange(record.Index + 1, count);
        }

        internal static string IdentifierName(Node node)
        {
            Identifier identifier = node as Identifier;
            return identifier == null ? null : identifier.Name;
        }

        internal static string PropertyName(Node node)
        {
            Identifier identifier = node as Identifier;
            if (identifier != null) return identifier.Name;
            Literal literal = node as Literal;
            return literal == null ? null : literal.StringValue;
        }

        internal static string StringValue(Node node)
        {
            Literal literal = node as Literal;
            if (literal != null) return literal.StringValue;
            TemplateLiteral template = node as TemplateLiteral;
            if (template == null || template.Expressions.Count != 0 || template.Quasis.Count != 1)
            {
                return null;
            }
            return template.Quasis[0].Value.Cooked;
        }

        internal static bool TryGetMember(
            Expression expression,
            out Expression target,
            out string property)
        {
            MemberExpression member = expression as MemberExpression;
            if (member == null)
            {
                target = null;
                property = null;
                return false;
            }
            target = member.Object;
            property = PropertyName(member.Property);
            return !string.IsNullOrWhiteSpace(property);
        }

        internal static string[] GetMemberChain(Expression expression)
        {
            List<string> parts = new List<string>();
            Expression current = expression;
            while (current != null)
            {
                Expression target;
                string property;
                if (!TryGetMember(current, out target, out property))
                {
                    string root = IdentifierName(current);
                    if (root == null && current is ThisExpression) root = "this";
                    if (root == null) return new string[0];
                    parts.Add(root);
                    break;
                }
                parts.Add(property);
                current = target;
            }
            parts.Reverse();
            return parts.ToArray();
        }

        internal static bool MemberChainEndsWith(Expression expression, params string[] suffix)
        {
            string[] chain = GetMemberChain(expression);
            if (suffix == null || chain.Length < suffix.Length) return false;
            int offset = chain.Length - suffix.Length;
            for (int index = 0; index < suffix.Length; index++)
            {
                if (!string.Equals(chain[offset + index], suffix[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private void Index(Node node, JavaScriptNodeRecord parent)
        {
            JavaScriptNodeRecord record = new JavaScriptNodeRecord(
                node,
                parent,
                records.Count);
            records.Add(record);
            recordsByNode.Add(node, record);
            foreach (Node child in node.ChildNodes)
            {
                Index(child, record);
            }
            record.SubtreeEndIndex = records.Count;
        }
    }

    internal sealed class JavaScriptNodeRecord
    {
        internal JavaScriptNodeRecord(
            Node node,
            JavaScriptNodeRecord parent,
            int index)
        {
            Node = node;
            Parent = parent;
            Index = index;
        }

        internal Node Node { get; private set; }
        internal JavaScriptNodeRecord Parent { get; private set; }
        internal int Index { get; private set; }
        internal int SubtreeEndIndex { get; set; }

        internal JavaScriptNodeRecord FindAncestor(Func<Node, bool> predicate)
        {
            JavaScriptNodeRecord current = Parent;
            while (current != null)
            {
                if (predicate(current.Node)) return current;
                current = current.Parent;
            }
            return null;
        }
    }
}
