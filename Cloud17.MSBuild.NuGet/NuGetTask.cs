using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Cloud17.MSBuild.NuGet
{
	public class NuGetTask : Task
	{
		#region  Public members

		private string PackagesConfig => $"{ProjectDir}packages.config";

		[Output]
		public string FileVersion { get; set; }

		#endregion

		private XmlElement GetElement(string elementName, XmlNode parentNode)
		{
			if (parentNode == null) throw new NullReferenceException("Parent node must be specified");

			var node = parentNode.SelectSingleNode(elementName) as XmlElement;
			if (node == null)
			{
				node = _nuspecDocument.CreateElement(elementName);
				parentNode.AppendChild(node);
			}

			return node;
		}

		public override bool Execute()
		{
			Log.LogMessage("Create nuspec file from: {0}", TargetPath);

			try
			{
				_nuspecDocument.Load(NuspecTemplate);

				_package = _nuspecDocument.SelectSingleNode("//package");
				_metaData = _nuspecDocument.SelectSingleNode("//package/metadata");
				_dependencies = GetElement("dependencies", _metaData);

				GetAssemblyMetaData();
				GetPackageDependencies();
				GetProjectDependencies();
				GetFiles();

				_nuspecDocument.Save($"{ProjectDir}{TargetName}.nuspec");

				return true;
			}
			catch (Exception e)
			{
				Log.LogErrorFromException(e);
				return false;
			}
		}

		private void GetAssemblyMetaData()
		{
			AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (s, e) =>
			{
				var directoryInfo = new FileInfo(e.RequestingAssembly.Location).Directory;
				if (directoryInfo != null)
				{
					var fAsm =
						new FileInfo(Path.Combine(directoryInfo.FullName,
							$"{e.Name.Substring(0, e.Name.IndexOf(",", StringComparison.Ordinal))}.dll"));
					if (fAsm.Exists)
					{
						return Assembly.ReflectionOnlyLoadFrom(fAsm.FullName);
					}
				}

				throw new FileNotFoundException($"Assembly {e.Name} not found");
			};

			_targetAssembly = Assembly.ReflectionOnlyLoadFrom(TargetPath);
			_version = _targetAssembly.GetName().Version.ToString();

			var attributes = CustomAttributeData.GetCustomAttributes(_targetAssembly);

			var titleAttr =
				attributes.SingleOrDefault(c => c.AttributeType == typeof (AssemblyTitleAttribute))?
					.ConstructorArguments.FirstOrDefault()
					.Value as string;
			var descAttr =
				attributes.SingleOrDefault(c => c.AttributeType == typeof (AssemblyDescriptionAttribute))?
					.ConstructorArguments.FirstOrDefault()
					.Value as string;
			var compAttr =
				attributes.SingleOrDefault(c => c.AttributeType == typeof (AssemblyCompanyAttribute))?
					.ConstructorArguments.FirstOrDefault()
					.Value as string;
			var copyAttr =
				attributes.SingleOrDefault(c => c.AttributeType == typeof (AssemblyCopyrightAttribute))?
					.ConstructorArguments.FirstOrDefault()
					.Value as string;
			var targetFrmAttr =
				attributes.SingleOrDefault(c => c.AttributeType == typeof (TargetFrameworkAttribute))?
					.ConstructorArguments.FirstOrDefault()
					.Value as string;
			var productVersion =
				attributes.SingleOrDefault(c => c.AttributeType == typeof (AssemblyInformationalVersionAttribute))?
					.ConstructorArguments.FirstOrDefault()
					.Value as string;

			if (targetFrmAttr != null)
			{
				_targetFramework =
					$"net{targetFrmAttr.Substring(targetFrmAttr.LastIndexOf("=v", StringComparison.Ordinal) + 2).Replace(".", "")}";
			}

			var idElm = _metaData.SelectSingleNode("id");
			var versionElm = _metaData.SelectSingleNode("version");
			var authorsElement = _metaData.SelectSingleNode("authors");
			var ownersElement = _metaData.SelectSingleNode("owners");
			var descElem = _metaData.SelectSingleNode("description");
			var copyElm = _metaData.SelectSingleNode("copyright");

			if (idElm != null && titleAttr != null)
				idElm.InnerText = titleAttr;

			if (versionElm != null && (productVersion != null || _version != null))
			{
				FileVersion = !string.IsNullOrEmpty(productVersion) ? productVersion : _version;
				versionElm.InnerText = FileVersion;
			}

			if (authorsElement != null && compAttr != null)
				authorsElement.InnerText = compAttr;

			if (ownersElement != null && compAttr != null)
				ownersElement.InnerText = compAttr;

			if (descElem != null && descAttr != null)
				descElem.InnerText = descAttr;

			if (copyElm != null && copyAttr != null)
				copyElm.InnerText = copyAttr;
		}

		private void GetPackageDependencies()
		{
			if (File.Exists(PackagesConfig))
			{
				_packagesDocument.Load(PackagesConfig);
				var packages = _packagesDocument.DocumentElement?.SelectNodes("//packages/package");

				if (packages != null)
				{
					foreach (XmlElement pac in packages)
					{
						if (pac.HasAttribute("id"))
						{
							var idAttr = pac.GetAttribute("id");
							string version = null;
							if (pac.HasAttribute("version"))
							{
								version = pac.GetAttribute("version");
							}

							AddDependency(idAttr, version);
						}
					}
				}
			}
		}

		private void AddDependency(string id, string version = null)
		{
			var depElm = _dependencies.SelectSingleNode($"dependency[@id='{id}']") as XmlElement;
			if (depElm == null)
			{
				depElm = _nuspecDocument.CreateElement("dependency");
				depElm.SetAttribute("id", id);

				if (version != null)
				{
					var versionAttr = version;
					depElm.SetAttribute("version", versionAttr);
				}
				_dependencies.AppendChild(depElm);
			}
		}

		private void GetProjectDependencies()
		{
			XNamespace msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";
			var projDefinition = XDocument.Load(ProjectPath);
			var xElement = projDefinition.Element(msbuild + "Project");
			if (xElement != null)
			{
				var projRefs = xElement
					.Elements(msbuild + "ItemGroup")
					.Elements(msbuild + "ProjectReference")
					.Select(refElem => refElem);

				foreach (var pj in projRefs)
				{
					var nameElm = pj.Element(msbuild + "Name");
					var inclAttr = pj.Attribute("Include");

					if (inclAttr != null && nameElm != null)
					{
						var projName = nameElm.Value;
						var refPath = Path.Combine(ProjectDir, inclAttr.Value);
						var projFile = new FileInfo(refPath);
						if (projFile.Exists)
						{
							if (projFile.DirectoryName != null)
							{
								var nuspecRefPath = Path.Combine(projFile.DirectoryName, projName + ".nuspec");
								if (File.Exists(nuspecRefPath))
								{
									string version = null;
									var nXml = new XmlDocument();
									nXml.Load(nuspecRefPath);
									var ver = nXml.DocumentElement?.SelectSingleNode("//package/metadata/version");
									var id = nXml.DocumentElement?.SelectSingleNode("//package/metadata/id");
									if (ver != null)
									{
										version = ver.InnerText;
									}

									if (!string.IsNullOrEmpty(id?.InnerText))
									{
										AddDependency(id.InnerText, version);
									}
								}
							}
						}
					}
				}
			}
		}

		private void GetFiles()
		{
			var files = _package.SelectSingleNode("files");
			if (files == null)
			{
				files = _nuspecDocument.CreateElement("files");
				_package.AppendChild(files);
			}

			var fi = new FileInfo(NuspecTemplate);
			var ft = new FileInfo(TargetPath);

			var toolsdir = $"{fi.DirectoryName}\\tools\\*";
			var toolsFile = _nuspecDocument.CreateElement("file");
			toolsFile.SetAttribute("src", toolsdir);
			toolsFile.SetAttribute("target", "tools");
			files.AppendChild(toolsFile);

			AddFile(ft.DirectoryName, TargetName, ".dll", "lib\\{0}\\{1}.dll", files);
			AddFile(ft.DirectoryName, TargetName, ".pdb", "lib\\{0}\\{1}.pdb", files);
			AddFile(ft.DirectoryName, TargetName, ".xml", "lib\\{0}\\{1}.xml", files);
		}

		private void AddFile(string targetDir, string targetName, string extension, string target, XmlNode filesElement)
		{
			var fullPath = $"{targetDir}\\{targetName}{extension}";
			if (File.Exists(fullPath))
			{
				var fileElm = _nuspecDocument.CreateElement("file");
				fileElm.SetAttribute("src", fullPath);
				fileElm.SetAttribute("target", string.Format(target, _targetFramework, targetName));

				filesElement.AppendChild(fileElm);
			}
		}

		#region Private members

		private readonly XmlDocument _nuspecDocument = new XmlDocument();
		private readonly XmlDocument _packagesDocument = new XmlDocument();
		private XmlNode _package;
		private XmlNode _metaData;
		private XmlNode _dependencies;
		private Assembly _targetAssembly;
		private string _version;
		private string _targetFramework = "net40";

		#endregion Private members

		#region Public properties

		[Required]
		public string TargetPath { get; set; }

		[Required]
		public string TargetName { get; set; }

		[Required]
		public string ProjectDir { get; set; }

		[Required]
		public string ProjectPath { get; set; }

		[Required]
		public string NuspecTemplate { get; set; }

		#endregion Public properties
	}
}