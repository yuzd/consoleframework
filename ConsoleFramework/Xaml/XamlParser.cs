﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;

namespace Xaml {
    /// <summary>
    /// Provides XAML parsing and simultaneous object graph creation.
    /// </summary>
    public class XamlParser {
        /// <summary>
        /// <para>Creates the object graph using provided xaml and dataContext.</para>
        /// <para>DataContext will be passed to markup extensions and can be null if you don't want to
        /// use binding markup extensions.</para>
        /// <para>Default namespaces are used to search types (by tag name) and
        /// markup extensions (all classes marked with MarkupExtensionAttribute are scanned).
        /// If don't specify default namespaces, you should specify namespaces (with prefixes)
        /// explicitly in XAML root element.</para>
        /// <para>Example of defaultNamespaces item:</para>
        /// <para><code>clr-namespace:TestProject1.Xaml.EnumsTest;assembly=TestProject1</code></para>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="xaml">Xaml markup</param>
        /// <param name="dataContext">Object that will be passed to markup extensions</param>
        /// <param name="defaultNamespaces">Namespaces can be used without explicit prefixes</param>
        /// <returns></returns>
        public static T CreateFromXaml<T>(string xaml, object dataContext, List<string> defaultNamespaces) {
            if (null == xaml)
                throw new ArgumentNullException(nameof(xaml));
            XamlParser parser = new XamlParser(defaultNamespaces);
            return (T) parser.createFromXaml(xaml, dataContext);
        }

        private XamlParser(List<string> defaultNamespaces) {
            this.defaultNamespaces = defaultNamespaces ?? throw new ArgumentNullException(nameof(defaultNamespaces));
        }

        private readonly List<string> defaultNamespaces;

        private class ObjectInfo {
            /// <summary>
            /// Type of constructing object.
            /// </summary>
            public Type type;

            /// <summary>
            /// Object instance (or null if String is created).
            /// </summary>
            public object obj;

            /// <summary>
            /// Current property that defined using tag with dot in name.
            /// &lt;Window.Resources&gt; for example
            /// </summary>
            public string currentProperty;

            /// <summary>
            /// For tags which content is text.
            /// </summary>
            public string currentPropertyText;

            /// <summary>
            /// Key set by x:Key attribute (if used)
            /// Will be used as key for this object in Dictionary-property of parent object
            /// </summary>
            public string key;

            /// <summary>
            /// Key set by x:Id attribute (if used)
            /// Object will be available from markup extensions using this id
            /// (for example, Ref markup extension)
            /// </summary>
            public string id;
        }

        private class MarkupExtensionsResolver : IMarkupExtensionsResolver {
            private readonly XamlParser self;

            public MarkupExtensionsResolver(XamlParser self) {
                this.self = self;
            }

            public Type Resolve(string name) {
                return self.resolveMarkupExtensionType(name);
            }
        }

        private class FixupToken : IFixupToken {
            /// <summary>
            /// String representation of markup extension that has returned this token
            /// </summary>
            public string Expression;

            /// <summary>
            /// Name of the property which is configuring by markup extension
            /// </summary>
            public string PropertyName;

            /// <summary>
            /// Object which property is configuring by markup extension
            /// </summary>
            public object Object;

            /// <summary>
            /// Data context passed to markup extension
            /// </summary>
            public object DataContext;

            /// <summary>
            /// List of objects' x:Id that were not found in current object graph,
            /// but are required to complete the execution of ProvideValue
            /// </summary>
            public IEnumerable<string> Ids;
        }

        private class MarkupExtensionContext : IMarkupExtensionContext {
            public string PropertyName { get; }
            public object Object { get; }

            public object DataContext { get; }

            //
            private readonly XamlParser self;
            private readonly string expression;

            public object GetObjectById(string id) {
                return self.objectsById.TryGetValue(id, out var value) ? value : null;
            }

            /// <summary>
            /// true means that parsing is not finished yet and new fixup tokens can be created
            /// false means that parsing is finished and no new objects will be constructed, so
            /// if markup extension can't find the reference to an object, it will not find it later
            /// And in this case the only way to proceed is throwing an exception
            /// </summary>
            public bool IsFixupTokenAvailable => self.objects.Count != 0;

            public IFixupToken GetFixupToken(IEnumerable<string> ids) {
                if (!IsFixupTokenAvailable)
                    throw new InvalidOperationException("Fixup tokens are not available now");
                return new FixupToken {
                    Expression = expression,
                    PropertyName = PropertyName,
                    Object = Object,
                    DataContext = DataContext,
                    Ids = ids
                };
            }

            public MarkupExtensionContext(XamlParser self, string expression, string propertyName,
                                          object obj, object dataContext) {
                this.self = self;
                this.expression = expression;
                this.PropertyName = propertyName;
                this.Object = obj;
                this.DataContext = dataContext;
            }
        }

        /// <summary>
        /// If text starts with single "{", method will treat str as markup extension and will process it.
        /// If text starts with "{}" prefix, method will treat the remaining suffix as string literal.
        /// So, you can use "{}" prefix as marker to disable markup extensions parser.
        /// </summary>
        private Object processText(string text, string currentProperty, object currentObject, object rootDataContext) {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            if (text[0] != '{') {
                // Treat whole text as string
                return text;
            }
            if (text.Length > 1 && text[1] == '}') {
                // Treat the rest as string
                return text.Length > 2 ? text.Substring(2) : string.Empty;
            }
            var parser = new MarkupExtensionsParser(new MarkupExtensionsResolver(this), text);
            var context = new MarkupExtensionContext(
                this, text, currentProperty, currentObject,
                findClosestDataContext() ?? rootDataContext
            );
            object providedValue = parser.ProcessMarkupExtension(context);
            if (providedValue is IFixupToken) {
                fixupTokens.Add((FixupToken) providedValue);
                // Null means no value will be assigned to target property
                return null;
            }
            return providedValue;
        }

        /// <summary>
        /// Tries to find the closest object in the stack with not null DataContext property.
        /// Returns first found data context object or null if no suitable objects found.
        /// </summary>
        private object findClosestDataContext() {
            foreach (var objectInfo in objects) {
                Type type = objectInfo.obj.GetType();
                PropertyInfo dataContextProp = type.GetProperty(getDataContextPropertyName(type));
                if (dataContextProp == null) {
                    continue;
                }
                object dataContextValue = dataContextProp.GetValue(objectInfo.obj);
                if (dataContextValue != null) {
                    return dataContextValue;
                }
            }
            return null;
        }

        private string getDataContextPropertyName(Type type) {
            object[] attributes = type.GetTypeInfo()
                .GetCustomAttributes(typeof(DataContextPropertyAttribute), true)
                .ToArray();
            if (attributes.Length == 0) {
                // Default value
                return "DataContext";
            }
            if (attributes.Length > 1) {
                throw new InvalidOperationException("Ambiguous data context property definition: " +
                                                    "more than one DataContextPropertyAttribute found");
            }
            return ((DataContextPropertyAttribute) attributes[0]).Name;
        }

        // Registered namespaces by prefix
        // { prefix -> namespace }
        private readonly Dictionary<string, string> namespaces = new Dictionary<string, string>();

        private object dataContext;

        /// <summary>
        /// Stack of configuring objects. Current configuring object is on top
        /// </summary>
        private readonly Stack<ObjectInfo> objects = new Stack<ObjectInfo>();

        /// <summary>
        /// Returns currently configuring object (or null if there is no current object)
        /// </summary>
        private ObjectInfo Top => objects.Count > 0 ? objects.Peek() : null;

        // Result object
        private object result;

        /// <summary>
        /// Map { x:Id -> object } of fully configured objects available to reference from
        /// markup extensions.
        /// </summary>
        private readonly Dictionary<String, Object> objectsById = new Dictionary<string, object>();

        /// <summary>
        /// List of fixup tokens used to defer objects by id resolving if markup extension
        /// has forward references to objects declared later.
        /// </summary>
        private readonly List<FixupToken> fixupTokens = new List<FixupToken>();

        /// <summary>
        /// Creates the object graph using provided xaml.
        /// </summary>
        /// <param name="xaml"></param>
        /// <param name="dataContext"></param>
        /// <returns></returns>
        private object createFromXaml(string xaml, object dataContext) {
            this.dataContext = dataContext;

            using (XmlReader xmlReader = XmlReader.Create(new StringReader(xaml))) {
                while (xmlReader.Read()) {
                    if (xmlReader.NodeType == XmlNodeType.Element) {
                        String name = xmlReader.Name;

                        // Explicit property syntax
                        if (Top != null && name.Contains(".")) {
                            // Type may be qualified with xmlns namespace
                            string typePrefix = name.Substring(0, name.IndexOf('.'));
                            Type type = resolveType(typePrefix);
                            if (type != Top.type) {
                                throw new Exception($"Property {name} doesn't match current object {Top.type}");
                            }
                            if (Top.currentProperty != null) {
                                throw new Exception("Illegal syntax in property value definition");
                            }
                            string propertyName = name.Substring(name.IndexOf('.') + 1);
                            Top.currentProperty = propertyName;
                        } else {
                            bool processingRootObject = (objects.Count == 0);

                            // Process namespace attributes if processing root object
                            if (processingRootObject && xmlReader.HasAttributes) {
                                if (xmlReader.HasAttributes) {
                                    while (xmlReader.MoveToNextAttribute()) {
                                        //
                                        string attributePrefix = xmlReader.Prefix;
                                        string attributeName = xmlReader.LocalName;
                                        string attributeValue = xmlReader.Value;

                                        // If we have found xmlns-attributes on root object, register them
                                        // in namespaces dictionary
                                        if (attributePrefix == "xmlns") {
                                            namespaces.Add(attributeName, attributeValue);
                                        } else if (attributePrefix == "" && attributeName == "xmlns") {
                                            // xmlns="" syntax support
                                            defaultNamespaces.Add(attributeValue);
                                        }
                                        //
                                    }
                                    xmlReader.MoveToElement();
                                }
                            }

                            objects.Push(createObject(name));

                            // Process attributes
                            if (xmlReader.HasAttributes) {
                                while (xmlReader.MoveToNextAttribute()) {
                                    //
                                    string attributePrefix = xmlReader.Prefix;
                                    string attributeName = xmlReader.LocalName;
                                    string attributeValue = xmlReader.Value;

                                    // Skip xmlns attributes of root object
                                    if ((attributePrefix == "xmlns"
                                         || attributePrefix == "" && attributeName == "xmlns")
                                        && processingRootObject) {
                                        continue;
                                    }

                                    processAttribute(attributePrefix, attributeName, attributeValue);
                                    //
                                }
                                xmlReader.MoveToElement();
                            }

                            if (xmlReader.IsEmptyElement)
                                processEndElement();
                        }
                    }

                    if (xmlReader.NodeType == XmlNodeType.Text) {
                        // This call moves xmlReader current element forward
                        Top.currentPropertyText = xmlReader.ReadContentAsString();
                    }

                    if (xmlReader.NodeType == XmlNodeType.EndElement) {
                        processEndElement();
                    }
                }
            }

            // After all the elements have been processed we call the markup extensions
            // (waiting for their forward-references) last time
            processFixupTokens();

            return result;
        }

        /// <summary>
        /// Aliases for primitive objects to avoid long declarations like
        /// &lt;xaml:Primitive x:TypeArg1="{Type System.Double}"&gt;&lt;/xaml:Primitive&gt;
        /// </summary>
        private static readonly Dictionary<String, Type> aliases = new Dictionary<string, Type> {
            {"object", typeof(ObjectFactory)},
            {"string", typeof(Primitive<string>)},
            {"int", typeof(Primitive<int>)},
            {"double", typeof(Primitive<double>)},
            {"float", typeof(Primitive<float>)},
            {"char", typeof(Primitive<char>)},
            {"bool", typeof(Primitive<bool>)}
        };

        private ObjectInfo createObject(string name) {
            Type type = aliases.ContainsKey(name) ? aliases[name] : resolveType(name);

            ConstructorInfo constructorInfo = type.GetConstructor(new Type[0]);
            if (null == constructorInfo)
                throw new Exception($"Type {type.FullName} has no default constructor");

            Object invoke = constructorInfo.Invoke(new object[0]);
            return new ObjectInfo {
                obj = invoke,
                type = type
            };
        }

        private void processAttribute(string attributePrefix, string attributeName, string attributeValue) {
            if (attributePrefix != string.Empty) {
                if (!namespaces.ContainsKey(attributePrefix))
                    throw new InvalidOperationException($"Unknown prefix {attributePrefix}");
                string namespaceUrl = namespaces[attributePrefix];
                if (namespaceUrl == "http://consoleframework.org/xaml.xsd") {
                    if (attributeName == "Key") {
                        Top.key = attributeValue;
                    } else if (attributeName == "Id") {
                        Top.id = attributeValue;
                    }
                }
            } else {
                // Process attribute as property assignment
                PropertyInfo propertyInfo = Top.type.GetProperty(attributeName);
                if (null == propertyInfo) {
                    throw new InvalidOperationException($"Property {attributeName} not found");
                }
                Object value = processText(attributeValue, attributeName, Top.obj, dataContext);
                if (null != value) {
                    object convertedValue = ConvertValueIfNeed(value.GetType(), propertyInfo.PropertyType, value);
                    propertyInfo.SetValue(Top.obj, convertedValue, null);
                }
            }
        }

        private String getContentPropertyName(Type type) {
            var attributes = type.GetTypeInfo().GetCustomAttributes(typeof(ContentPropertyAttribute), true).ToArray();
            if (attributes.Length == 0)
                return "Content";
            if (attributes.Length > 1)
                throw new InvalidOperationException("Ambiguous content property definition: " +
                                                    "more than one ContentPropertyAttribute found");
            return ((ContentPropertyAttribute) attributes[0]).Name;
        }

        /// <summary>
        /// Finishes configuring current object and assigns it to property of parent object
        /// </summary>
        private void processEndElement() {
            bool assignToParent;

            // Closed element having text content
            if (Top.currentPropertyText != null) {
                PropertyInfo property = Top.currentProperty != null
                    ? Top.type.GetProperty(Top.currentProperty)
                    : Top.type.GetProperty(getContentPropertyName(Top.type));
                Object value = processText(Top.currentPropertyText, Top.currentProperty, Top.obj, dataContext);
                if (value != null) {
                    Object convertedValue = ConvertValueIfNeed(value.GetType(), property.PropertyType, value);
                    property.SetValue(Top.obj, convertedValue, null);
                }
                if (Top.currentProperty != null) {
                    Top.currentProperty = null;
                    assignToParent = false;
                } else {
                    // For the objects declared using text content (for example <MyObject>text</MyObject>)
                    // currentProperty is null. When we meet closing tag </MyObject> we should do 2 things:
                    // 1) Assign "text" to the Content-property of object
                    // 2) Assign constructed object to property of parent object
                    // These steps will make this markup equivalent to
                    // <MyObject><MyObject.Content>text</MyObject.Content></MyObject>
                    // (what we need exactly)
                    assignToParent = true;
                }
                Top.currentPropertyText = null;
            } else {
                assignToParent = true;
            }

            if (!assignToParent)
                return;

            // Closed element having sub-element content
            if (Top.currentProperty != null) {
                // Property-tag was closed, child element is already assigned to property,
                // so there is nothing to do. Just assign currentProperty to null
                Top.currentProperty = null;
            } else {
                // Main tag for current constructing object was closed
                // We need to get the object from upper level and assign
                // the property of it to created object (or add to collection if it is collection)
                ObjectInfo initialized = objects.Pop();

                if (initialized.obj is IFactory factory)
                    initialized.obj = factory.GetObject();

                if (objects.Count == 0) {
                    result = initialized.obj;
                } else {
                    string propertyName = Top.currentProperty ?? getContentPropertyName(Top.type);

                    // If parent object property is ICollection<T>,
                    // add current object into them as T (will conversion if need)
                    PropertyInfo property = Top.type.GetProperty(propertyName);
                    Type typeArg1 = property.PropertyType.GetTypeInfo().IsGenericType
                        ? property.PropertyType.GetGenericArguments()[0]
                        : null;
                    if (null != typeArg1 &&
                        typeof(ICollection<>).MakeGenericType(typeArg1).IsAssignableFrom(property.PropertyType)) {
                        object collection = property.GetValue(Top.obj, null);
                        MethodInfo methodInfo = collection.GetType().GetMethod("Add");
                        object converted = ConvertValueIfNeed(initialized.obj.GetType(), typeArg1, initialized.obj);
                        methodInfo.Invoke(collection, new[] {converted});
                    } else {
                        // If parent object property is IList add current object into them without conversion
                        if (typeof(IList).IsAssignableFrom(property.PropertyType)) {
                            IList list = (IList) property.GetValue(Top.obj, null);
                            list.Add(initialized.obj);
                        } else {
                            // If parent object property is IDictionary<string, T>,
                            // add current object into them (by x:Key value) 
                            // with conversion to T if need
                            Type typeArg2 = property.PropertyType.GetTypeInfo().IsGenericType &&
                                            property.PropertyType.GetGenericArguments().Length > 1
                                ? property.PropertyType.GetGenericArguments()[1]
                                : null;
                            if (null != typeArg1 && typeArg1 == typeof(string) && null != typeArg2
                                && typeof(IDictionary<,>).MakeGenericType(typeArg1, typeArg2)
                                    .IsAssignableFrom(property.PropertyType)) {
                                object dictionary = property.GetValue(Top.obj, null);
                                MethodInfo methodInfo = dictionary.GetType().GetMethod("Add");
                                object converted = ConvertValueIfNeed(initialized.obj.GetType(),
                                    typeArg2, initialized.obj);
                                if (null == initialized.key)
                                    throw new InvalidOperationException("Key is not specified for item of dictionary");
                                methodInfo.Invoke(dictionary, new[] {initialized.key, converted});
                            } else {
                                // Handle as property - call setter with conversion if need
                                property.SetValue(
                                    Top.obj,
                                    ConvertValueIfNeed(initialized.obj.GetType(), property.PropertyType,
                                        initialized.obj),
                                    null
                                );
                            }
                        }
                    }
                }

                // If object has x:Id, add them to objectsById map
                if (initialized.id != null) {
                    if (objectsById.ContainsKey(initialized.id))
                        throw new InvalidOperationException($"Object with Id={initialized.id} redefinition");
                    objectsById.Add(initialized.id, initialized.obj);

                    processFixupTokens();
                }
            }
        }

        private void processFixupTokens() {
            // Search satisfied fixup tokens and call markup extensions again for them
            List<FixupToken> tokens = new List<FixupToken>(fixupTokens);
            fixupTokens.Clear();
            foreach (FixupToken token in tokens) {
                if (token.Ids.All(id => objectsById.ContainsKey(id))) {
                    MarkupExtensionsParser markupExtensionsParser = new MarkupExtensionsParser(
                        new MarkupExtensionsResolver(this), token.Expression);
                    MarkupExtensionContext context = new MarkupExtensionContext(
                        this, token.Expression, token.PropertyName, token.Object, token.DataContext);
                    object providedValue = markupExtensionsParser.ProcessMarkupExtension(context);
                    if (providedValue is IFixupToken) {
                        fixupTokens.Add((FixupToken) providedValue);
                    } else {
                        // Assign providedValue to property of object
                        if (null != providedValue) {
                            PropertyInfo propertyInfo = token.Object.GetType().GetProperty(token.PropertyName);
                            object convertedValue = ConvertValueIfNeed(
                                providedValue.GetType(), propertyInfo.PropertyType, providedValue);
                            propertyInfo.SetValue(token.Object, convertedValue, null);
                        }
                    }
                } else {
                    fixupTokens.Add(token);
                }
            }
        }

        /// <summary>
        /// Converts the value from source type to destination if need
        /// using default conversion strategies and registered type converters.
        /// </summary>
        /// <param name="source">Type of source value</param>
        /// <param name="dest">Type of destination</param>
        /// <param name="value">Source value</param>
        internal static object ConvertValueIfNeed(Type source, Type dest, object value) {
            if (dest.IsAssignableFrom(source)) {
                return value;
            }

            // Process enumerations
            // todo : add TypeConverterAttribute support on enum, and unit tests
            if (source == typeof(String) && dest.GetTypeInfo().IsEnum) {
                string[] enumNames = Enum.GetNames(dest);
                for (int i = 0, len = enumNames.Length; i < len; i++) {
                    if (enumNames[i] == (String) value) {
                        return Enum.GetValues(dest).GetValue(i);
                    }
                }
                throw new Exception($"Specified enum value {value} not found");
            }

            // todo : default converters for primitives
            if (source == typeof(string) && dest == typeof(bool)) {
                return bool.Parse((string) value);
            }
            if (source == typeof(string) && dest == typeof(int)) {
                return int.Parse((string) value);
            }
            if (source == typeof(string) && dest == typeof(int?)) {
                return int.Parse((string) value);
            }
            if (source == typeof(string) && dest == typeof(char?))
            {
                return string.IsNullOrEmpty((string) value) ? (char?) null : ((string) value)[0];
            }

            // Process TypeConverterAttribute attributes if exist
            if (Type.GetTypeCode(source) == TypeCode.Object) {
                object[] attributes = source.GetTypeInfo()
                    .GetCustomAttributes(typeof(TypeConverterAttribute), true)
                    .ToArray();
                if (attributes.Length > 1)
                    throw new InvalidOperationException("Ambiguous attribute: more than one TypeConverterAttribute");
                if (attributes.Length == 1) {
                    TypeConverterAttribute attribute = (TypeConverterAttribute) attributes[0];
                    Type typeConverterType = attribute.Type;
                    ConstructorInfo ctor = typeConverterType.GetConstructor(new Type[0]);
                    if (null == ctor) {
                        throw new InvalidOperationException($"No default constructor in {typeConverterType.Name} type");
                    }
                    ITypeConverter converter = (ITypeConverter) ctor.Invoke(new object[0]);
                    if (converter.CanConvertTo(dest)) {
                        return converter.ConvertTo(value, dest);
                    }
                }
            }

            if (Type.GetTypeCode(dest) == TypeCode.Object) {
                var attributes = dest.GetTypeInfo().GetCustomAttributes(typeof(TypeConverterAttribute), true).ToArray();
                if (attributes.Length > 1)
                    throw new InvalidOperationException("Ambiguous attribute: more than one TypeConverterAttribute");
                if (attributes.Length == 1) {
                    TypeConverterAttribute attribute = (TypeConverterAttribute) attributes[0];
                    Type typeConverterType = attribute.Type;
                    ConstructorInfo ctor = typeConverterType.GetConstructor(new Type[0]);
                    if (null == ctor)
                        throw new InvalidOperationException($"No default constructor in {typeConverterType.Name} type");
                    ITypeConverter converter = (ITypeConverter) ctor.Invoke(new object[0]);
                    if (converter.CanConvertFrom(source)) {
                        return converter.ConvertFrom(value);
                    }
                }
            }

            throw new NotSupportedException();
        }

        private Type resolveMarkupExtensionType(string name) {
            string bindingName;
            var namespacesToScan = getNamespacesToScan(name, out bindingName);

            // Scan namespaces
            Type resultType = null;
            foreach (string ns in namespacesToScan) {
                Regex regex = new Regex("clr-namespace:(.+);assembly=(.+)");
                MatchCollection matchCollection = regex.Matches(ns);
                if (matchCollection.Count == 0)
                    throw new InvalidOperationException($"Invalid clr-namespace syntax: {ns}");
                string namespaceName = matchCollection[0].Groups[1].Value;
                string assemblyName = matchCollection[0].Groups[2].Value;

                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                List<Type> types = assembly.GetTypes().Where(type => {
                    if (type.Namespace != namespaceName)
                        return false;
                    object[] attributes = type.GetTypeInfo()
                        .GetCustomAttributes(typeof(MarkupExtensionAttribute), true)
                        .ToArray();
                    return attributes.Any(o => ((MarkupExtensionAttribute) o).Name == bindingName);
                }).ToList();

                if (types.Count > 1)
                    throw new InvalidOperationException("More than one markup extension" +
                                                        $" for name {name} in namespace {ns}");
                if (types.Count == 1) {
                    resultType = types[0];
                    break;
                }
            }

            if (resultType == null)
                throw new InvalidOperationException($"Cannot resolve markup extension {name}");
            return resultType;
        }

        /// <summary>
        /// Takes input of the type name and returns a Type object corresponding to it.
        /// The type name can be either with the (qualified by) prefix, or without it.
        /// If type name contains a prefix, then type will be searched in the corresponding clr-namespace.
        /// Otherwise the search will be executed in a set of default namespaces (defaultNamespaces)
        /// that are set in the constructor of the XamlParser class.
        /// </summary>
        private Type resolveType(string name) {
            string typeName;
            var namespacesToScan = getNamespacesToScan(name, out typeName);

            // Scan namespaces
            Type resultType = null;
            foreach (string ns in namespacesToScan) {
                Regex regex = new Regex("clr-namespace:(.+);assembly=(.+)");
                MatchCollection matchCollection = regex.Matches(ns);
                if (matchCollection.Count == 0)
                    throw new InvalidOperationException($"Invalid clr-namespace syntax: {ns}");
                string namespaceName = matchCollection[0].Groups[1].Value;
                string assemblyName = matchCollection[0].Groups[2].Value;

                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                List<Type> types = assembly.GetTypes()
                    .Where(type => type.Namespace == namespaceName && type.Name == typeName)
                    .ToList();
                if (types.Count > 1)
                    throw new InvalidOperationException("Assertion error");
                if (types.Count == 1) {
                    resultType = types[0];
                    break;
                }
            }

            if (resultType == null)
                throw new InvalidOperationException($"Cannot resolve type {name}");
            return resultType;
        }

        /// <summary>
        /// Returns list of namespaces to scan for name.
        /// If name is prefixed, namespaces will be that was registered for this prefix.
        /// If name is without prefix, default namespaces will be returned.
        /// </summary>
        private IEnumerable<string> getNamespacesToScan(string name, out string unprefixedName) {
            List<string> namespacesToScan;
            if (name.Contains(":")) {
                var prefix = name.Substring(0, name.IndexOf(':'));
                if (name.IndexOf(':') + 1 >= name.Length)
                    throw new InvalidOperationException($"Invalid type name {name}");
                unprefixedName = name.Substring(name.IndexOf(':') + 1);
                if (!namespaces.ContainsKey(prefix))
                    throw new InvalidOperationException($"Unknown prefix {prefix}");
                namespacesToScan = new List<string> {namespaces[prefix]};
            } else {
                namespacesToScan = defaultNamespaces;
                unprefixedName = name;
            }
            return namespacesToScan;
        }
    }
}