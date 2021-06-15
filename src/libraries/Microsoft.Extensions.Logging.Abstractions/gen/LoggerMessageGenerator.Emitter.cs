// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Microsoft.Extensions.Logging.Generators
{
    public partial class LoggerMessageGenerator
    {
        internal class Emitter
        {
            // The maximum arity of LoggerMessage.Define.
            private const int MaxLoggerMessageDefineArguments = 6;
            private const int DefaultStringBuilderCapacity = 1024;

            private static readonly string s_generatedCodeAttribute =
                $"global::System.CodeDom.Compiler.GeneratedCodeAttribute(" +
                $"\"{typeof(Emitter).Assembly.GetName().Name}\", " +
                $"\"{typeof(Emitter).Assembly.GetName().Version}\")";
            private readonly StringBuilder _builder = new StringBuilder(DefaultStringBuilderCapacity);
            private bool _needEnumerationHelper;

            public string Emit(IReadOnlyList<LoggerClass> logClasses, CancellationToken cancellationToken)
            {
                _builder.Clear();
                _builder.AppendLine("// <auto-generated/>");
                _builder.AppendLine("#nullable enable");

                foreach (LoggerClass lc in logClasses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    GenType(lc);
                }

                GenEnumerationHelper();
                return _builder.ToString();
            }

            private static bool UseLoggerMessageDefine(LoggerMethod lm)
            {
                bool result =
                    (lm.TemplateParameters.Count <= MaxLoggerMessageDefineArguments) && // more args than LoggerMessage.Define can handle
                    (lm.Level != null) &&                                               // dynamic log level, which LoggerMessage.Define can't handle
                    (lm.TemplateList.Count == lm.TemplateParameters.Count);             // mismatch in template to args, which LoggerMessage.Define can't handle

                if (result)
                {
                    // make sure the order of the templates matches the order of the logging method parameter
                    for (int i = 0; i < lm.TemplateList.Count; i++)
                    {
                        string t = lm.TemplateList[i];
                        if (!t.Equals(lm.TemplateParameters[i].Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // order doesn't match, can't use LoggerMessage.Define
                            return false;
                        }
                    }
                }

                return result;
            }

            private void GenType(LoggerClass lc)
            {
                int indentationSize = 0;
                string nestedIndentation = "";
                if (!string.IsNullOrWhiteSpace(lc.Namespace))
                {
                    _builder.Append($@"
namespace {lc.Namespace}
{{");
                }

                LoggerClass parent = lc.ParentClass;
                var parentClasses = new List<string>();
                // loop until you find top level nested class
                while (parent != null)
                {
                    parentClasses.Add($"partial class {parent?.Name + " " + parent?.Constraints}");
                    parent = parent.ParentClass;
                }

                // write down top level nested class first
                for (int i = parentClasses.Count - 1; i >= 0; i--)
                {
                    _builder.Append($@"
    {nestedIndentation}{parentClasses[i]}
    {nestedIndentation}{{");
                    indentationSize += 4;
                    nestedIndentation = new String(' ', indentationSize);
                }

                _builder.Append($@"
    {nestedIndentation}partial class {lc.Name} {lc.Constraints}
    {nestedIndentation}{{");

                foreach (LoggerMethod lm in lc.Methods)
                {
                    if (!UseLoggerMessageDefine(lm))
                    {
                        GenStruct(lm, nestedIndentation);
                    }

                    GenLogMethod(lm, nestedIndentation);
                }

                _builder.Append($@"
    {nestedIndentation}}}");

                parent = lc.ParentClass;
                while (parent != null)
                {
                    indentationSize -= 4;
                    nestedIndentation = new String(' ', indentationSize);
                    _builder.Append($@"
    {nestedIndentation}}}");
                    parent = parent.ParentClass;
                }

                if (!string.IsNullOrWhiteSpace(lc.Namespace))
                {
                    _builder.Append($@"
}}");
                }
            }

            private void GenStruct(LoggerMethod lm, string nestedIndentation)
            {
                _builder.AppendLine($@"
        {nestedIndentation}[{s_generatedCodeAttribute}]
        {nestedIndentation}private readonly struct __{lm.Name}Struct : global::System.Collections.Generic.IReadOnlyList<global::System.Collections.Generic.KeyValuePair<string, object?>>
        {nestedIndentation}{{");
                GenFields(lm, nestedIndentation);

                if (lm.TemplateParameters.Count > 0)
                {
                    _builder.Append($@"
            {nestedIndentation}public __{lm.Name}Struct(");
                    GenArguments(lm);
                    _builder.Append($@")
            {nestedIndentation}{{");
                    _builder.AppendLine();
                    GenFieldAssignments(lm, nestedIndentation);
                    _builder.Append($@"
            {nestedIndentation}}}
");
                }

                _builder.Append($@"
            {nestedIndentation}public override string ToString()
            {nestedIndentation}{{
");
                GenVariableAssignments(lm, nestedIndentation);
                _builder.Append($@"
                {nestedIndentation}return $""{lm.Message}"";
            {nestedIndentation}}}
");
                _builder.Append($@"
            {nestedIndentation}public static string Format(__{lm.Name}Struct state, global::System.Exception? ex) => state.ToString();

            {nestedIndentation}public int Count => {lm.TemplateParameters.Count + 1};

            {nestedIndentation}public global::System.Collections.Generic.KeyValuePair<string, object?> this[int index]
            {nestedIndentation}{{
                {nestedIndentation}get => index switch
                {nestedIndentation}{{
");
                GenCases(lm, nestedIndentation);
                _builder.Append($@"
                    {nestedIndentation}_ => throw new global::System.IndexOutOfRangeException(nameof(index)),  // return the same exception LoggerMessage.Define returns in this case
                {nestedIndentation}}};
            }}

            {nestedIndentation}public global::System.Collections.Generic.IEnumerator<global::System.Collections.Generic.KeyValuePair<string, object?>> GetEnumerator()
            {nestedIndentation}{{
                {nestedIndentation}for (int i = 0; i < {lm.TemplateParameters.Count + 1}; i++)
                {nestedIndentation}{{
                    {nestedIndentation}yield return this[i];
                {nestedIndentation}}}
            {nestedIndentation}}}

            {nestedIndentation}global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        {nestedIndentation}}}
");
            }

            private void GenFields(LoggerMethod lm, string nestedIndentation)
            {
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    _builder.AppendLine($"            {nestedIndentation}private readonly {p.Type} _{p.Name};");
                }
            }

            private void GenFieldAssignments(LoggerMethod lm, string nestedIndentation)
            {
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    _builder.AppendLine($"                {nestedIndentation}this._{p.Name} = {p.Name};");
                }
            }

            private void GenVariableAssignments(LoggerMethod lm, string nestedIndentation)
            {
                foreach (KeyValuePair<string, string> t in lm.TemplateMap)
                {
                    int index = 0;
                    foreach (LoggerParameter p in lm.TemplateParameters)
                    {
                        if (t.Key.Equals(p.Name, System.StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        index++;
                    }

                    // check for an index that's too big, this can happen in some cases of malformed input
                    if (index < lm.TemplateParameters.Count)
                    {
                        if (lm.TemplateParameters[index].IsEnumerable)
                        {
                            _builder.AppendLine($"                {nestedIndentation}var {t.Key} = "
                                + $"global::__LoggerMessageGenerator.Enumerate((global::System.Collections.IEnumerable ?)this._{lm.TemplateParameters[index].Name});");

                            _needEnumerationHelper = true;
                        }
                        else
                        {
                            _builder.AppendLine($"                {nestedIndentation}var {t.Key} = this._{lm.TemplateParameters[index].Name};");
                        }
                    }
                }
            }

            private void GenCases(LoggerMethod lm, string nestedIndentation)
            {
                int index = 0;
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    string name = p.Name;
                    if (lm.TemplateMap.ContainsKey(name))
                    {
                        // take the letter casing from the template
                        name = lm.TemplateMap[name];
                    }

                    _builder.AppendLine($"                    {nestedIndentation}{index++} => new global::System.Collections.Generic.KeyValuePair<string, object?>(\"{name}\", this._{p.Name}),");
                }

                _builder.AppendLine($"                    {nestedIndentation}{index++} => new global::System.Collections.Generic.KeyValuePair<string, object?>(\"{{OriginalFormat}}\", \"{ConvertEndOfLineAndQuotationCharactersToEscapeForm(lm.Message)}\"),");
            }

            private void GenCallbackArguments(LoggerMethod lm)
            {
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    _builder.Append($"{p.Name}, ");
                }
            }

            private void GenDefineTypes(LoggerMethod lm, bool brackets)
            {
                if (lm.TemplateParameters.Count == 0)
                {
                    return;
                }
                if (brackets)
                {
                    _builder.Append('<');
                }

                bool firstItem = true;
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    if (firstItem)
                    {
                        firstItem = false;
                    }
                    else
                    {
                        _builder.Append(", ");
                    }

                    _builder.Append($"{p.Type}");
                }

                if (brackets)
                {
                    _builder.Append('>');
                }
                else
                {
                    _builder.Append(", ");
                }
            }

            private void GenParameters(LoggerMethod lm)
            {
                bool firstItem = true;
                foreach (LoggerParameter p in lm.AllParameters)
                {
                    if (firstItem)
                    {
                        firstItem = false;
                    }
                    else
                    {
                        _builder.Append(", ");
                    }

                    _builder.Append($"{p.Type} {p.Name}");
                }
            }

            private void GenArguments(LoggerMethod lm)
            {
                bool firstItem = true;
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    if (firstItem)
                    {
                        firstItem = false;
                    }
                    else
                    {
                        _builder.Append(", ");
                    }

                    _builder.Append($"{p.Type} {p.Name}");
                }
            }

            private void GenHolder(LoggerMethod lm)
            {
                string typeName = $"__{lm.Name}Struct";

                _builder.Append($"new {typeName}(");
                foreach (LoggerParameter p in lm.TemplateParameters)
                {
                    if (p != lm.TemplateParameters[0])
                    {
                        _builder.Append(", ");
                    }

                    _builder.Append(p.Name);
                }

                _builder.Append(')');
            }

            private void GenLogMethod(LoggerMethod lm, string nestedIndentation)
            {
                string level = GetLogLevel(lm);
                string extension = lm.IsExtensionMethod ? "this " : string.Empty;
                string eventName = string.IsNullOrWhiteSpace(lm.EventName) ? $"nameof({lm.Name})" : $"\"{lm.EventName}\"";
                string exceptionArg = GetException(lm);
                string logger = GetLogger(lm);

                if (UseLoggerMessageDefine(lm))
                {
                    _builder.Append($@"
        {nestedIndentation}[{s_generatedCodeAttribute}]
        {nestedIndentation}private static readonly global::System.Action<global::Microsoft.Extensions.Logging.ILogger, ");

                    GenDefineTypes(lm, brackets: false);

                    _builder.Append($@"global::System.Exception?> __{lm.Name}Callback =
            {nestedIndentation}global::Microsoft.Extensions.Logging.LoggerMessage.Define");

                    GenDefineTypes(lm, brackets: true);

                    _builder.Append(@$"({level}, new global::Microsoft.Extensions.Logging.EventId({lm.EventId}, {eventName}), ""{ConvertEndOfLineAndQuotationCharactersToEscapeForm(lm.Message)}"", true); 
");
                }

                _builder.Append($@"
        {nestedIndentation}[{s_generatedCodeAttribute}]
        {nestedIndentation}{lm.Modifiers} void {lm.Name}({extension}");

                GenParameters(lm);

                _builder.Append($@")
        {nestedIndentation}{{
            {nestedIndentation}if ({logger}.IsEnabled({level}))
            {nestedIndentation}{{");

                if (UseLoggerMessageDefine(lm))
                {
                    _builder.Append($@"
                {nestedIndentation}__{lm.Name}Callback({logger}, ");

                    GenCallbackArguments(lm);

                    _builder.Append(@$"{exceptionArg});");
                }
                else
                {
                    _builder.Append($@"
                {nestedIndentation}{logger}.Log(
                    {level},
                    new global::Microsoft.Extensions.Logging.EventId({lm.EventId}, {eventName}),
                    ");
                    GenHolder(lm);
                    _builder.Append($@",
                    {exceptionArg},
                    __{lm.Name}Struct.Format);");
                }

                _builder.Append($@"
            {nestedIndentation}}}
        {nestedIndentation}}}");

                static string GetException(LoggerMethod lm)
                {
                    string exceptionArg = "null";
                    foreach (LoggerParameter p in lm.AllParameters)
                    {
                        if (p.IsException)
                        {
                            exceptionArg = p.Name;
                            break;
                        }
                    }
                    return exceptionArg;
                }

                static string GetLogger(LoggerMethod lm)
                {
                    string logger = lm.LoggerField;
                    foreach (LoggerParameter p in lm.AllParameters)
                    {
                        if (p.IsLogger)
                        {
                            logger = p.Name;
                            break;
                        }
                    }
                    return logger;
                }

                static string GetLogLevel(LoggerMethod lm)
                {
                    string level = string.Empty;

                    if (lm.Level == null)
                    {
                        foreach (LoggerParameter p in lm.AllParameters)
                        {
                            if (p.IsLogLevel)
                            {
                                level = p.Name;
                                break;
                            }
                        }
                    }
                    else
                    {
                        level = lm.Level switch
                        {
                            0 => "global::Microsoft.Extensions.Logging.LogLevel.Trace",
                            1 => "global::Microsoft.Extensions.Logging.LogLevel.Debug",
                            2 => "global::Microsoft.Extensions.Logging.LogLevel.Information",
                            3 => "global::Microsoft.Extensions.Logging.LogLevel.Warning",
                            4 => "global::Microsoft.Extensions.Logging.LogLevel.Error",
                            5 => "global::Microsoft.Extensions.Logging.LogLevel.Critical",
                            6 => "global::Microsoft.Extensions.Logging.LogLevel.None",
                            _ => $"(global::Microsoft.Extensions.Logging.LogLevel){lm.Level}",
                        };
                    }

                    return level;
                }
            }

            private void GenEnumerationHelper()
            {
                if (_needEnumerationHelper)
                {
                                _builder.Append($@"
[{s_generatedCodeAttribute}]
internal static class __LoggerMessageGenerator
{{
    public static string Enumerate(global::System.Collections.IEnumerable? enumerable)
    {{
        if (enumerable == null)
        {{
            return ""(null)"";
        }}

        var sb = new global::System.Text.StringBuilder();
        _ = sb.Append('[');

        bool first = true;
        foreach (object e in enumerable)
        {{
            if (!first)
            {{
                _ = sb.Append("", "");
            }}

            if (e == null)
            {{
                _ = sb.Append(""(null)"");
            }}
            else
            {{
                if (e is global::System.IFormattable fmt)
                {{
                    _ = sb.Append(fmt.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture));
                }}
                else
                {{
                    _ = sb.Append(e);
                }}
            }}

            first = false;
        }}

        _ = sb.Append(']');

        return sb.ToString();
    }}
}}");
                }
            }
        }

        private static string ConvertEndOfLineAndQuotationCharactersToEscapeForm(string s)
        {
            int index = 0;
            while (index < s.Length)
            {
                if (s[index] == '\n' || s[index] == '\r' || s[index] == '"')
                {
                    break;
                }
                index++;
            }

            if (index >= s.Length)
            {
                return s;
            }

            StringBuilder sb = new StringBuilder(s.Length);
            sb.Append(s, 0, index);

            while (index < s.Length)
            {
                switch (s[index])
                {
                    case '\n':
                        sb.Append('\\');
                        sb.Append('n');
                        break;

                    case '\r':
                        sb.Append('\\');
                        sb.Append('r');
                        break;

                    case '"':
                        sb.Append('\\');
                        sb.Append('"');
                        break;

                    default:
                        sb.Append(s[index]);
                        break;
                }

                index++;
            }

            return sb.ToString();
        }
    }
}
