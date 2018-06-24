﻿// Copyright (c) 2018 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.TypeSystem.Implementation
{
	sealed class MetadataMethod : IMethod
	{
		readonly MetadataAssembly assembly;
		readonly MethodDefinitionHandle handle;

		// eagerly loaded fields:
		readonly MethodAttributes attributes;
		readonly SymbolKind symbolKind;
		readonly ITypeParameter[] typeParameters;
		public bool IsExtensionMethod { get; }

		// lazy-loaded fields:
		ITypeDefinition declaringType;
		string name;
		IAttribute[] customAttributes;
		IAttribute[] returnTypeAttributes;
		IParameter[] parameters;
		IType returnType;

		internal MetadataMethod(MetadataAssembly assembly, MethodDefinitionHandle handle)
		{
			Debug.Assert(assembly != null);
			Debug.Assert(!handle.IsNil);
			this.assembly = assembly;
			this.handle = handle;
			var metadata = assembly.metadata;
			var def = metadata.GetMethodDefinition(handle);
			this.attributes = def.Attributes;

			this.symbolKind = SymbolKind.Method;
			if ((attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) != 0) {
				string name = this.Name;
				if (name == ".cctor" || name == ".ctor")
					this.symbolKind = SymbolKind.Constructor;
				else if (name.StartsWith("op_", StringComparison.Ordinal))
					this.symbolKind = SymbolKind.Operator;
			}
			this.typeParameters = MetadataTypeParameter.Create(assembly, this, def.GetGenericParameters());
			this.IsExtensionMethod = (attributes & MethodAttributes.Static) == MethodAttributes.Static
				&& (assembly.TypeSystemOptions & TypeSystemOptions.ExtensionMethods) == TypeSystemOptions.ExtensionMethods
				&& def.GetCustomAttributes().HasKnownAttribute(metadata, KnownAttribute.Extension);
		}

		public EntityHandle MetadataToken => handle;

		public override string ToString()
		{
			return $"{MetadataTokens.GetToken(handle):X8} {DeclaringType?.ReflectionName}.{Name}";
		}

		public string Name {
			get {
				string name = LazyInit.VolatileRead(ref this.name);
				if (name != null)
					return name;
				var metadata = assembly.metadata;
				var methodDef = metadata.GetMethodDefinition(handle);
				return LazyInit.GetOrSet(ref this.name, metadata.GetString(methodDef.Name));
			}
		}

		public IReadOnlyList<ITypeParameter> TypeParameters => typeParameters;
		IReadOnlyList<IType> IMethod.TypeArguments => typeParameters;

		public SymbolKind SymbolKind => symbolKind;
		public bool IsConstructor => symbolKind == SymbolKind.Constructor;
		public bool IsDestructor => symbolKind == SymbolKind.Destructor;
		public bool IsOperator => symbolKind == SymbolKind.Operator;
		public bool IsAccessor => symbolKind == SymbolKind.Accessor;

		public bool HasBody => assembly.metadata.GetMethodDefinition(handle).HasBody();


		public IMember AccessorOwner => throw new NotImplementedException();

		#region Signature (ReturnType + Parameters)
		public IReadOnlyList<IParameter> Parameters {
			get {
				var parameters = LazyInit.VolatileRead(ref this.parameters);
				if (parameters != null)
					return parameters;
				DecodeSignature();
				return this.parameters;
			}
		}

		public IType ReturnType {
			get {
				var returnType = LazyInit.VolatileRead(ref this.returnType);
				if (returnType != null)
					return returnType;
				DecodeSignature();
				return this.returnType;
			}
		}

		private void DecodeSignature()
		{
			var metadata = assembly.metadata;
			var methodDef = metadata.GetMethodDefinition(handle);
			var genericContext = new GenericContext(DeclaringType.TypeParameters, this.TypeParameters);
			var signature = methodDef.DecodeSignature(assembly.TypeProvider, genericContext);
			int i = 0;
			CustomAttributeHandleCollection? returnTypeAttributes = null;
			IParameter[] parameters = new IParameter[signature.RequiredParameterCount
				+ (signature.Header.CallingConvention == SignatureCallingConvention.VarArgs ? 1 : 0)];
			foreach (var parameterHandle in methodDef.GetParameters()) {
				var par = metadata.GetParameter(parameterHandle);
				if (par.SequenceNumber == 0) {
					// "parameter" holds return type attributes
					returnTypeAttributes = par.GetCustomAttributes();
				} else if (par.SequenceNumber > 0 && i < signature.RequiredParameterCount) {
					Debug.Assert(par.SequenceNumber - 1 == i);
					var parameterType = ApplyAttributeTypeVisitor.ApplyAttributesToType(
						signature.ParameterTypes[i], Compilation,
						par.GetCustomAttributes(), metadata, assembly.TypeSystemOptions);
					parameters[i] = new MetadataParameter(assembly, this, parameterType, parameterHandle);
					i++;
				}
			}
			while (i < signature.RequiredParameterCount) {
				var parameterType = ApplyAttributeTypeVisitor.ApplyAttributesToType(
					signature.ParameterTypes[i], Compilation, null, metadata, assembly.TypeSystemOptions);
				parameters[i] = new DefaultParameter(parameterType, name: string.Empty, owner: this,
					isRef: parameterType.Kind == TypeKind.ByReference);
				i++;
			}
			if (signature.Header.CallingConvention == SignatureCallingConvention.VarArgs) {
				parameters[i] = new DefaultParameter(SpecialType.ArgList, name: string.Empty, owner: this);
				i++;
			}
			Debug.Assert(i == parameters.Length);
			var returnType = ApplyAttributeTypeVisitor.ApplyAttributesToType(signature.ReturnType,
				Compilation, returnTypeAttributes, metadata, assembly.TypeSystemOptions);
			LazyInit.GetOrSet(ref this.returnType, returnType);
			LazyInit.GetOrSet(ref this.parameters, parameters);
		}
		#endregion

		public IReadOnlyList<IMember> ImplementedInterfaceMembers => throw new NotImplementedException();

		public bool IsExplicitInterfaceImplementation => throw new NotImplementedException();

		IMember IMember.MemberDefinition => this;
		IMethod IMethod.ReducedFrom => this;
		TypeParameterSubstitution IMember.Substitution => TypeParameterSubstitution.Identity;

		public ITypeDefinition DeclaringTypeDefinition {
			get {
				var declType = LazyInit.VolatileRead(ref this.declaringType);
				if (declType != null) {
					return declType;
				} else {
					var def = assembly.metadata.GetMethodDefinition(handle);
					return LazyInit.GetOrSet(ref this.declaringType,
						assembly.GetDefinition(def.GetDeclaringType()));
				}
			}
		}

		public IType DeclaringType => DeclaringTypeDefinition;

		public IAssembly ParentAssembly => assembly;
		public ICompilation Compilation => assembly.Compilation;

		#region Attributes
		public IReadOnlyList<IAttribute> Attributes {
			get {
				var attr = LazyInit.VolatileRead(ref this.customAttributes);
				if (attr != null)
					return attr;
				return LazyInit.GetOrSet(ref this.customAttributes, DecodeAttributes());
			}
		}

		IType FindInteropType(string name)
		{
			return assembly.Compilation.FindType(new TopLevelTypeName(
				"System.Runtime.InteropServices", name, 0
			));
		}

		IAttribute[] DecodeAttributes()
		{
			var b = new AttributeListBuilder(assembly);

			var metadata = assembly.metadata;
			var def = metadata.GetMethodDefinition(handle);
			MethodImplAttributes implAttributes = def.ImplAttributes & ~MethodImplAttributes.CodeTypeMask;

			#region DllImportAttribute
			var info = def.GetImport();
			if ((attributes & MethodAttributes.PinvokeImpl) == MethodAttributes.PinvokeImpl && !info.Module.IsNil) {
				var dllImportType = assembly.GetAttributeType(KnownAttribute.DllImport);
				var positionalArguments = new ResolveResult[] {
					new ConstantResolveResult(assembly.Compilation.FindType(KnownTypeCode.String),
						metadata.GetString(metadata.GetModuleReference(info.Module).Name))
				};
				var namedArgs = new List<KeyValuePair<IMember, ResolveResult>>();

				var importAttrs = info.Attributes;
				if ((importAttrs & MethodImportAttributes.BestFitMappingDisable) == MethodImportAttributes.BestFitMappingDisable)
					namedArgs.Add(b.MakeNamedArg(dllImportType, "BestFitMapping", false));
				if ((importAttrs & MethodImportAttributes.BestFitMappingEnable) == MethodImportAttributes.BestFitMappingEnable)
					namedArgs.Add(b.MakeNamedArg(dllImportType, "BestFitMapping", true));

				CallingConvention callingConvention;
				switch (info.Attributes & MethodImportAttributes.CallingConventionMask) {
					case 0:
						Debug.WriteLine($"P/Invoke calling convention not set on: {this}");
						callingConvention = 0;
						break;
					case MethodImportAttributes.CallingConventionCDecl:
						callingConvention = CallingConvention.Cdecl;
						break;
					case MethodImportAttributes.CallingConventionFastCall:
						callingConvention = CallingConvention.FastCall;
						break;
					case MethodImportAttributes.CallingConventionStdCall:
						callingConvention = CallingConvention.StdCall;
						break;
					case MethodImportAttributes.CallingConventionThisCall:
						callingConvention = CallingConvention.ThisCall;
						break;
					case MethodImportAttributes.CallingConventionWinApi:
						callingConvention = CallingConvention.Winapi;
						break;
					default:
						throw new NotSupportedException("unknown calling convention");
				}
				if (callingConvention != CallingConvention.Winapi) {
					var callingConventionType = FindInteropType(nameof(CallingConvention));
					namedArgs.Add(b.MakeNamedArg(dllImportType, "CallingConvention", callingConventionType, (int)callingConvention));
				}

				CharSet charSet = CharSet.None;
				switch (info.Attributes & MethodImportAttributes.CharSetMask) {
					case MethodImportAttributes.CharSetAnsi:
						charSet = CharSet.Ansi;
						break;
					case MethodImportAttributes.CharSetAuto:
						charSet = CharSet.Auto;
						break;
					case MethodImportAttributes.CharSetUnicode:
						charSet = CharSet.Unicode;
						break;
				}
				if (charSet != CharSet.None) {
					var charSetType = FindInteropType(nameof(CharSet));
					namedArgs.Add(b.MakeNamedArg(dllImportType, "CharSet", charSetType, (int)charSet));
				}

				if (!info.Name.IsNil && info.Name != def.Name) {
					namedArgs.Add(b.MakeNamedArg(dllImportType,
						"EntryPoint", KnownTypeCode.String, metadata.GetString(info.Name)));
				}

				if ((info.Attributes & MethodImportAttributes.ExactSpelling) == MethodImportAttributes.ExactSpelling) {
					namedArgs.Add(b.MakeNamedArg(dllImportType, "ExactSpelling", true));
				}

				if ((implAttributes & MethodImplAttributes.PreserveSig) == MethodImplAttributes.PreserveSig) {
					implAttributes &= ~MethodImplAttributes.PreserveSig;
				} else {
					namedArgs.Add(b.MakeNamedArg(dllImportType, "PreserveSig", true));
				}

				if ((info.Attributes & MethodImportAttributes.SetLastError) == MethodImportAttributes.SetLastError)
					namedArgs.Add(b.MakeNamedArg(dllImportType, "SetLastError", true));

				if ((info.Attributes & MethodImportAttributes.ThrowOnUnmappableCharDisable) == MethodImportAttributes.ThrowOnUnmappableCharDisable)
					namedArgs.Add(b.MakeNamedArg(dllImportType, "ThrowOnUnmappableChar", false));
				if ((info.Attributes & MethodImportAttributes.ThrowOnUnmappableCharEnable) == MethodImportAttributes.ThrowOnUnmappableCharEnable)
					namedArgs.Add(b.MakeNamedArg(dllImportType, "ThrowOnUnmappableChar", true));

				b.Add(new DefaultAttribute(dllImportType, positionalArguments, namedArgs));
			}
			#endregion

			#region PreserveSigAttribute
			if (implAttributes == MethodImplAttributes.PreserveSig) {
				b.Add(KnownAttribute.PreserveSig);
				implAttributes = 0;
			}
			#endregion

			#region MethodImplAttribute
			if (implAttributes != 0) {
				b.Add(KnownAttribute.MethodImpl, new ConstantResolveResult(
					Compilation.FindType(new TopLevelTypeName("System.Runtime.CompilerServices", nameof(MethodImplOptions))),
					(int)implAttributes
				));
			}
			#endregion

			b.Add(def.GetCustomAttributes());
			b.AddSecurityAttributes(def.GetDeclarativeSecurityAttributes());

			return b.Build();
		}
		#endregion

		#region Return type attributes
		public IReadOnlyList<IAttribute> ReturnTypeAttributes {
			get {
				var attr = LazyInit.VolatileRead(ref this.returnTypeAttributes);
				if (attr != null)
					return attr;
				return LazyInit.GetOrSet(ref this.returnTypeAttributes, DecodeReturnTypeAttributes());
			}
		}

		private IAttribute[] DecodeReturnTypeAttributes()
		{
			var b = new AttributeListBuilder(assembly);
			var metadata = assembly.metadata;
			var methodDefinition = metadata.GetMethodDefinition(handle);
			var parameters = methodDefinition.GetParameters();
			if (parameters.Count > 0) {
				var retParam = metadata.GetParameter(parameters.First());
				if (retParam.SequenceNumber == 0) {
					b.AddMarshalInfo(retParam.GetMarshallingDescriptor());
					b.Add(retParam.GetCustomAttributes());
				}
			}
			return b.Build();
		}
		#endregion

		public Accessibility Accessibility => GetAccessibility(attributes);
		
		internal static Accessibility GetAccessibility(MethodAttributes attr)
		{
			switch (attr & MethodAttributes.MemberAccessMask) {
				case MethodAttributes.Public:
					return Accessibility.Public;
				case MethodAttributes.Assembly:
					return Accessibility.Internal;
				case MethodAttributes.Private:
					return Accessibility.Private;
				case MethodAttributes.Family:
					return Accessibility.Protected;
				case MethodAttributes.FamANDAssem:
					return Accessibility.ProtectedAndInternal;
				case MethodAttributes.FamORAssem:
					return Accessibility.ProtectedOrInternal;
				default:
					return Accessibility.None;
			}
		}

		public bool IsStatic => (attributes & MethodAttributes.Static) != 0;
		public bool IsAbstract => (attributes & MethodAttributes.Abstract) != 0;
		public bool IsSealed => (attributes & (MethodAttributes.Abstract | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.Static)) == MethodAttributes.Final;
		public bool IsVirtual => (attributes & (MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot)) == (MethodAttributes.Virtual | MethodAttributes.NewSlot);
		public bool IsOverride => (attributes & (MethodAttributes.NewSlot | MethodAttributes.Static)) == 0;
		public bool IsOverridable
			=> (attributes & (MethodAttributes.Abstract | MethodAttributes.Virtual)) != 0
			&& (attributes & MethodAttributes.Final) == 0;

		bool IEntity.IsShadowing => throw new NotImplementedException();

		public string FullName => $"{DeclaringType?.FullName}.{Name}";
		public string ReflectionName => $"{DeclaringType?.ReflectionName}.{Name}";
		public string Namespace => DeclaringType?.Namespace ?? string.Empty;

		bool IMember.Equals(IMember obj, TypeVisitor typeNormalization)
		{
			return obj == this;
		}

		public IMethod Specialize(TypeParameterSubstitution substitution)
		{
			return SpecializedMethod.Create(this, substitution);
		}

		IMember IMember.Specialize(TypeParameterSubstitution substitution)
		{
			return SpecializedMethod.Create(this, substitution);
		}
	}
}