using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using K4os.Compression.LZ4;
using UnityDataTools.FileSystem;

namespace UnityDataTools.TextDumper;

public class TextDumperTool
{
    StringBuilder m_StringBuilder = new StringBuilder(1024);
    bool m_SkipLargeArrays;
    UnityFileReader m_Reader;
    SerializedFile m_SerializedFile;
    StreamWriter m_Writer;

    public int Dump(string path, string outputPath, bool skipLargeArrays)
    {
        m_SkipLargeArrays = skipLargeArrays;

        try
        {
            try
            {
                using var archive = UnityFileSystem.MountArchive(path, "/");
                foreach (var node in archive.Nodes)
                {
                    Console.WriteLine($"Processing {node.Path} {node.Size} {node.Flags}");

                    if (node.Flags.HasFlag(ArchiveNodeFlags.SerializedFile))
                    {
                        using (m_Writer = new StreamWriter(Path.Combine(outputPath, Path.GetFileName(node.Path) + ".txt"), false))
                        {
                            OutputSerializedFile("/" + node.Path);
                        }
                    }
                }
            }
            catch (NotSupportedException)
            {
                // Try as SerializedFile
                using (m_Writer = new StreamWriter(Path.Combine(outputPath, Path.GetFileName(path) + ".txt"), false))
                {
                    OutputSerializedFile(path);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Error!");
            Console.Write($"{e.GetType()}: ");
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            return 1;
        }

        return 0;
    }

    void RecursiveDump(TmpMap map, TypeTreeNode node, ref long offset, int level, int arrayIndex = -1)
    {
        bool skipChildren = false;

        if (!node.IsArray)
        {
            map.Name = node.Name;

            m_StringBuilder.Append(' ', level * 2);

            if (level != 0)
            {
                m_StringBuilder.Append(node.Name);
                if (arrayIndex >= 0)
                {
                    m_StringBuilder.Append('[');
                    m_StringBuilder.Append(arrayIndex);
                    m_StringBuilder.Append(']');
                }
                m_StringBuilder.Append(' ');
                m_StringBuilder.Append('(');
                m_StringBuilder.Append(node.Type);
                m_StringBuilder.Append(')');
            }
            else
            {
                m_StringBuilder.Append(node.Type);
            }

            // Basic data type.
            if (node.IsBasicType)
            {
                m_StringBuilder.Append(' ');
                m_StringBuilder.Append(map.Value = ReadValue(node, offset));

                offset += node.Size;
            }
            else if (node.Type == "string")
            {
                var stringSize = m_Reader.ReadInt32(offset);

                m_StringBuilder.Append(' ');
                m_StringBuilder.Append(map.Value = m_Reader.ReadString(offset + 4, stringSize));

                offset += stringSize + 4;

                // Skip child nodes as they were already processed here.
                skipChildren = true;
            }

            m_Writer.WriteLine(m_StringBuilder);
            m_StringBuilder.Clear();
            
            if (node.IsManagedReferenceRegistry)
            {
                DumpManagedReferenceRegistry(node, ref offset, level + 1);

                // Skip child nodes as they were already processed here.
                skipChildren = true;
            }
        }
        else
        {
            DumpArray(map, node, ref offset, level);

            // Skip child nodes as they were already processed here.
            skipChildren = true;
        }

        if (!skipChildren)
        {
            map.Map = new Dictionary<string, TmpMap>();
            foreach (var child in node.Children)
            {
                if(child.IsArray)
                    RecursiveDump(map, child, ref offset, level + 1);
                else
                {
                    var cMap = new TmpMap();
                    RecursiveDump(cMap, child, ref offset, level + 1);
                    map.Map[cMap.Name] = cMap;
                }
            }
        }

        if (
            ((int)node.MetaFlags & (int)TypeTreeMetaFlags.AlignBytes) != 0 ||
            ((int)node.MetaFlags & (int)TypeTreeMetaFlags.AnyChildUsesAlignBytes) != 0
        )
        {
            offset = (offset + 3) & ~(3);
        }
    }

    void DumpArray(TmpMap map, TypeTreeNode node, ref long offset, int level)
    {
        // First child contains array size.
        var sizeNode = node.Children[0];
        // Second child contains array type information.
        var dataNode = node.Children[1];

        if (sizeNode.Size != 4 || !sizeNode.IsLeaf)
            throw new Exception("Invalid array size");

        var arraySize = m_Reader.ReadInt32(offset);
        offset += 4;

        m_StringBuilder.Append(' ', level * 2);
        m_StringBuilder.Append("Array");
        m_StringBuilder.Append('<');
        m_StringBuilder.Append(dataNode.Type);
        m_StringBuilder.Append(">[");
        m_StringBuilder.Append(arraySize);
        m_StringBuilder.Append(']');

        m_Writer.WriteLine(m_StringBuilder);
        m_StringBuilder.Clear();

        map.List = new List<TmpMap>();
        if (arraySize > 0)
        {
            if (dataNode.IsBasicType)
            {
                m_StringBuilder.Append(' ', (level + 1) * 2);

                if (arraySize > 256 && m_SkipLargeArrays)
                {
                    m_StringBuilder.Append("<Skipped>");
                    offset += dataNode.Size * arraySize;
                }
                else
                {
                    var array = ReadBasicTypeArray(dataNode, offset, arraySize);
                    offset += dataNode.Size * arraySize;


                    m_StringBuilder.Append(array.GetValue(0));
                    map.List.Add(new TmpMap { Value = $"{array.GetValue(0)}" });
                    for (int i = 1; i < arraySize; ++i)
                    {
                        m_StringBuilder.Append(", ");
                        m_StringBuilder.Append(array.GetValue(i));
                        map.List.Add(new TmpMap { Value = $"{array.GetValue(i)}" });
                    }
                }

                m_Writer.WriteLine(m_StringBuilder);
                m_StringBuilder.Clear();
            }
            else
            {
                ++level;
                
                for (int i = 0; i < arraySize; ++i)
                {
                    var cMap = new TmpMap();
                    RecursiveDump(cMap, dataNode, ref offset, level, i);
                    map.List.Add(cMap);
                }
            }
        }
    }

    void DumpManagedReferenceRegistry(TypeTreeNode node, ref long offset, int level)
    {
        if (node.Children.Count < 2)
            throw new Exception("Invalid ManagedReferenceRegistry");
                
        // First child is version number.
        var version = m_Reader.ReadInt32(offset);
        RecursiveDump(new (), node.Children[0], ref offset, level);

        TypeTreeNode refTypeNode;
        TypeTreeNode refObjData;
                
        if (version == 1)
        {
            // Second child is the ReferencedObject.
            var refObjNode = node.Children[1];
            // And its children are the referenced type and data nodes.
            refTypeNode = refObjNode.Children[0];
            refObjData = refObjNode.Children[1];
                
            int i = 0;

            while (DumpManagedReferenceData(refTypeNode, refObjData, ref offset, level, i++))
            {}
        }
        else if (version == 2)
        {
            // Second child is the RefIds vector.
            var refIdsVectorNode = node.Children[1];

            if (refIdsVectorNode.Children.Count < 1 || refIdsVectorNode.Name != "RefIds")
                throw new Exception("Invalid ManagedReferenceRegistry RefIds vector");

            var refIdsArrayNode = refIdsVectorNode.Children[0];

            if (refIdsArrayNode.Children.Count != 2 || !refIdsArrayNode.IsArray)
                throw new Exception("Invalid ManagedReferenceRegistry RefIds array");

            // First child is the array size.
            int arraySize = m_Reader.ReadInt32(offset);
            offset += 4;
                
            // Second child is the ReferencedObject.
            var refObjNode = refIdsArrayNode.Children[1];

            for (int i = 0; i < arraySize; ++i)
            {
                // First child is the rid.
                long rid = m_Reader.ReadInt64(offset);
                offset += 8;
                
                // And the next children are the referenced type and data nodes.
                refTypeNode = refObjNode.Children[1];
                refObjData = refObjNode.Children[2];
                DumpManagedReferenceData(refTypeNode, refObjData, ref offset, level, rid);
            }
        }
        else
        {
            throw new Exception("Unsupported ManagedReferenceRegistry version");
        }
    }

    bool DumpManagedReferenceData(TypeTreeNode refTypeNode, TypeTreeNode referencedTypeDataNode, ref long offset, int level, long id)
    {
        if (refTypeNode.Children.Count < 3)
            throw new Exception("Invalid ReferencedManagedType");
            
        m_StringBuilder.Append(' ', level * 2);
        m_StringBuilder.Append($"rid(");
        m_StringBuilder.Append(id);
        m_StringBuilder.Append(") ReferencedObject");
        
        m_Writer.WriteLine(m_StringBuilder);
        m_StringBuilder.Clear();
        
        ++level;

        var refTypeOffset = offset;
        var stringSize = m_Reader.ReadInt32(offset);
        var className = m_Reader.ReadString(offset + 4, stringSize);
        offset += stringSize + 4;
        offset = (offset + 3) & ~(3);
            
        stringSize = m_Reader.ReadInt32(offset);
        var namespaceName = m_Reader.ReadString(offset + 4, stringSize);
        offset += stringSize + 4;
        offset = (offset + 3) & ~(3);
            
        stringSize = m_Reader.ReadInt32(offset);
        var assemblyName = m_Reader.ReadString(offset + 4, stringSize);
        offset += stringSize + 4;
        offset = (offset + 3) & ~(3);

        if (className == "Terminus" && namespaceName == "UnityEngine.DMAT" && assemblyName == "FAKE_ASM")
            return false;

        // Not the most efficient way, but it simplifies the code.
        RecursiveDump(new (), refTypeNode, ref refTypeOffset, level);

        m_StringBuilder.Append(' ', level * 2);
        m_StringBuilder.Append(referencedTypeDataNode.Name);
        m_StringBuilder.Append(' ');
        m_StringBuilder.Append(referencedTypeDataNode.Type);
        m_StringBuilder.Append(' ');
        
        m_Writer.WriteLine(m_StringBuilder);
        m_StringBuilder.Clear();

        if (id == -1 || id == -2)
        {
            m_StringBuilder.Append(' ', level * 2);
            m_StringBuilder.Append(id == -1 ? "  unknown" : "  null");
        
            m_Writer.WriteLine(m_StringBuilder);
            m_StringBuilder.Clear();

            return true;
        }

        var refTypeRoot = m_SerializedFile.GetRefTypeTypeTreeRoot(className, namespaceName, assemblyName);
        
        // Dump the ReferencedObject using its own TypeTree, but skip the root.
        foreach (var child in refTypeRoot.Children)
        {
            RecursiveDump(new (), child, ref offset, level + 1);
        }

        return true;
    }

    void OutputSerializedFile(string path)
    {
        using (m_Reader = new UnityFileReader(path, 64 * 1024 * 1024))
        using (m_SerializedFile = UnityFileSystem.OpenSerializedFile(path))
        {
            var i = 1;

            m_Writer.WriteLine("External References");
            foreach (var extRef in m_SerializedFile.ExternalReferences)
            {
                m_Writer.WriteLine($"path({i}): \"{extRef.Path}\" GUID: {extRef.Guid} Type: {(int)extRef.Type}");
                ++i;
            }
            m_Writer.WriteLine();

            foreach (var obj in m_SerializedFile.Objects)
            {
                var root = m_SerializedFile.GetTypeTreeRoot(obj.Id);
                var offset = obj.Offset;

                m_Writer.Write($"ID: {obj.Id} (ClassID: {obj.TypeId}) ");
                var map = new TmpMap();
                RecursiveDump(map, root, ref offset, 0);
                m_Writer.WriteLine();

                if(obj.TypeId == 48)
                {
                    var outputPath = ((FileStream)m_Writer.BaseStream).Name;
                    var outputPath2 = Path.ChangeExtension(outputPath, ".shader.txt");
                    var info = CollectShader(map);
                    File.WriteAllText(outputPath2, info.ToString());

                    var outputPath3 = Path.ChangeExtension(outputPath, ".shader_source.txt");
                    File.WriteAllBytes(outputPath3, info.decompressedBlob);
                }
            }
        }
    }

    class TmpMap
    {
        public string Name;
        public string Value;
        public Dictionary<string, TmpMap> Map;
        public List<TmpMap> List;

        public TmpMap this[int index] => List[index];
        public TmpMap this[string key] => Map[key];
    }

    static ShaderInfo CollectShader(TmpMap map)
    {
        var info = new ShaderInfo
        {
            Name = map["m_ParsedForm"]["m_Name"].Value,
            Props = map["m_ParsedForm"]["m_PropInfo"]["m_Props"].List.Select(x => x["m_Name"].Value).ToList(),
            Keywords = map["m_ParsedForm"]["m_KeywordNames"].List.Select(x => x.Value).ToList(),
            compressedBlob = map["compressedBlob"].List.Select(x => byte.Parse(x.Value)).ToArray(),
            decompressedBlob = new byte[int.Parse(map["decompressedLengths"][0][0].Value)],
        };
        LZ4Codec.Decode(info.compressedBlob, info.decompressedBlob);
        info.SubShaders = map["m_ParsedForm"]["m_SubShaders"].List.Select(subShader =>
        {
            var ssInfo = new ShaderInfo.SubShaderInfo
            {
                Passes = subShader["m_Passes"].List.Select(pass =>
                {
                    var passInfo = new ShaderInfo.SubShaderInfo.PassInfo
                    {
                        ProgramsVS = A(pass, "Vertex", info.Keywords),
                        ProgramsPS = A(pass, "Fragment", info.Keywords)
                    };
                    static List<ShaderInfo.SubShaderInfo.PassInfo.ProgInfo> A(TmpMap pass, string stage, IList<string> kw)
                    {
                        return pass[$"prog{stage}"]["m_PlayerSubPrograms"].List.Select(sProgs =>
                        {
                            var progInfo = new ShaderInfo.SubShaderInfo.PassInfo.ProgInfo();
                            progInfo.variants = sProgs.List.Select(x =>
                            {
                                return new ShaderInfo.SubShaderInfo.PassInfo.ProgInfo.VariantInfo
                                {
                                    keywords = x["m_KeywordIndices"].List.Select(x => kw[int.Parse(x.Value)]).ToArray(),
                                    blobOffset = int.Parse(x["m_BlobIndex"].Value),
                                };
                            }).ToList();
                            return progInfo;
                        }).ToList();
                    }
                    return passInfo;
                }).ToList()
            };
            return ssInfo;
        }).ToList();
        return info;
    }

    class ShaderInfo
    {
        public string Name;
        public List<SubShaderInfo> SubShaders = new ();
        public List<string> Keywords = new ();
        public List<string> Props = new ();
        public byte[] compressedBlob;
        public byte[] decompressedBlob;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Shader:");
            sb.AppendLine($"  {Name}");
            sb.AppendLine("Props:");
            foreach (var prop in Props)
            {
                sb.AppendLine($"  {prop}");
            }
            sb.AppendLine("Keywords:");
            foreach (var keyword in Keywords)
            {
                sb.AppendLine($"  {keyword}");
            }
            sb.AppendLine();
            sb.AppendLine("====================================");
            sb.AppendLine();
            sb.AppendLine("SubShaders");
            for(int i = 0; i < SubShaders.Count; i++)
            {
                sb.AppendLine($"#SubShader {i}");
                sb.AppendLine(SubShaders[i].ToString());
            }
            return sb.ToString();
        }

        public class SubShaderInfo
        {
            public List<PassInfo> Passes = new ();
            public override string ToString()
            {
                var sb = new StringBuilder();
                for (int i = 0; i < Passes.Count; i++)
                {
                    sb.AppendLine($"#Pass {i}");
                    sb.AppendLine(Passes[i].ToString());
                }
                return sb.ToString();
            }

            public class PassInfo
            {
                public List<ProgInfo> ProgramsVS = new ();
                public List<ProgInfo> ProgramsPS = new ();
                public override string ToString()
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("VS");
                    foreach (var variant in ProgramsVS.SelectMany(x => x.variants))
                    {
                        sb.AppendLine($"  {variant}");
                    }
                    sb.AppendLine("PS");
                    foreach (var variant in ProgramsPS.SelectMany(x => x.variants))
                    {
                        sb.AppendLine($"  {variant}");
                    }
                    return sb.ToString();
                }

                public class ProgInfo
                {
                    public List<VariantInfo> variants = new ();

                    public class VariantInfo
                    {
                        public string[] keywords;
                        public int blobOffset;
                        public override string ToString() => $"+{blobOffset} {string.Join(" ", keywords)}";
                    }
                }
            }
        }
    }

    string ReadValue(TypeTreeNode node, long offset)
    {
        switch (Type.GetTypeCode(node.CSharpType))
        {
            case TypeCode.Int32:
                return m_Reader.ReadInt32(offset).ToString();

            case TypeCode.UInt32:
                return m_Reader.ReadUInt32(offset).ToString();

            case TypeCode.Single:
                return m_Reader.ReadFloat(offset).ToString();

            case TypeCode.Double:
                return m_Reader.ReadDouble(offset).ToString();

            case TypeCode.Int16:
                return m_Reader.ReadInt16(offset).ToString();

            case TypeCode.UInt16:
                return m_Reader.ReadUInt16(offset).ToString();

            case TypeCode.Int64:
                return m_Reader.ReadInt64(offset).ToString();

            case TypeCode.UInt64:
                return m_Reader.ReadUInt64(offset).ToString();

            case TypeCode.SByte:
                return m_Reader.ReadUInt8(offset).ToString();

            case TypeCode.Byte:
            case TypeCode.Char:
                return m_Reader.ReadUInt8(offset).ToString();

            case TypeCode.Boolean:
                return (m_Reader.ReadUInt8(offset) != 0).ToString();

            default:
                throw new Exception($"Can't get value of {node.Type} type");
        }
    }

    Array ReadBasicTypeArray(TypeTreeNode node, long offset, int arraySize)
    {
        // Special case for boolean arrays.
        if (node.CSharpType == typeof(bool))
        {
            var tmpArray = new byte[arraySize];
            var boolArray = new bool[arraySize];

            m_Reader.ReadArray(offset, arraySize * node.Size, tmpArray);

            for (int i = 0; i < arraySize; ++i)
            {
                boolArray[i] = tmpArray[i] != 0;
            }

            return boolArray;
        }
        else
        {
            var array = Array.CreateInstance(node.CSharpType, arraySize);

            m_Reader.ReadArray(offset, arraySize * node.Size, array);

            return array;
        }
    }
}