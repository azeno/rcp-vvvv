#region usings
using System;
using System.IO;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V2.Graph;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;
using VVVV.Core.Logging;

using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using V4 = System.Numerics.Vector4;

using RCP;
using RCP.Model;
using RCP.Transporter;
using RCP.Parameter;
using RCP.Protocol;

using Kaitai;

#endregion usings

namespace VVVV.Nodes
{
	#region PluginInfo
	[PluginInfo(Name = "Rabbit",
	Category = "RCP",
	AutoEvaluate = true,
	Help = "An RCP Server",
	Tags = "remote, server")]
	#endregion PluginInfo
	public class RCPRabbitNode : IPluginEvaluate, IPartImportsSatisfiedNotification, IDisposable
	{
		#region fields & pins
		[Input("Host", IsSingle=true, DefaultString = "127.0.0.1")]
		public IDiffSpread<string> FHost; 
		
		[Input("Port", IsSingle=true, DefaultValue = 10000)]
		public IDiffSpread<int> FPort; 
		
		[Input("Update Enums", IsSingle=true, IsBang=true)]
		public ISpread<bool> FUpdateEnums; 
		
		[Output("Client Count")]
		public ISpread<int> FConnectionCount;
		//public ISpread<byte> FOutput;
		
		[Import()]
		public ILogger FLogger;
		
		[Import()]
		public IHDEHost FHDEHost;
		
		RCPServer FRCPServer;
		WebsocketServerTransporter FTransporter;
		Dictionary<string, IGroupParameter> FGroups = new Dictionary<string, IGroupParameter>();
		Dictionary<string, IPin2> FCachedPins = new Dictionary<string, IPin2>();
		Dictionary<string, IParameter> FCachedParams = new Dictionary<string, IParameter>();
		Dictionary<string, List<IParameter>> FParameters = new Dictionary<string, List<IParameter>>();
		
		List<IParameter> FParameterQueue = new List<IParameter>();
		#endregion fields & pins
		  
		public RCPRabbitNode()
		{ 
			//initialize the RCP Server
			FRCPServer = new RCPServer();
		}
		
		public void OnImportsSatisfied()
		{
			FHDEHost.ExposedNodeService.NodeAdded += NodeAddedCB;
			FHDEHost.ExposedNodeService.NodeRemoved += NodeRemovedCB;
			
			GroupMap.GroupAdded += GroupAdded; 
			GroupMap.GroupRemoved += GroupRemoved; 
			
			//FRCPServer.Log = (s) => FLogger.Log(LogType.Debug, "server: " + s);
			 
			//get initial list of exposed ioboxes
			foreach (var node in FHDEHost.ExposedNodeService.Nodes)
				NodeAddedCB(node);
		}
		
		public void Dispose()
		{
			//unscubscribe from nodeservice
			FHDEHost.ExposedNodeService.NodeAdded -= NodeAddedCB;
			FHDEHost.ExposedNodeService.NodeRemoved -= NodeRemovedCB;
			
			GroupMap.GroupAdded -= GroupAdded; 
			GroupMap.GroupRemoved -= GroupRemoved; 
			
			//dispose the RCP server
			FLogger.Log(LogType.Debug, "Disposing the RCP Server");
			FRCPServer.Dispose();
			
			//clear cached pins
			FGroups.Clear();
			FCachedPins.Clear();
			FCachedParams.Clear();
			//FNodeToIdMap.Clear();
			
			FParameterQueue.Clear();
		}
		
		private void GroupAdded(string address)
		{
			if (!FGroups.ContainsKey(address))
			{
				var group = FRCPServer.CreateGroup(GroupMap.GetName(address));
				FGroups.Add(address, group);
				
				//FLogger.Log(LogType.Debug, "group added: " + group.Label + " #" + group.Id.ToString());
				
				//move all ioboxes of the groups patch to the group
				var ps = GetGroupParameters(address);
				foreach (var param in ps)
				{
					//FLogger.Log(LogType.Debug, "to " + group.Label + ": " + param.Label);
					FRCPServer.AddParameter(param, group);
				}	
				
				FRCPServer.Update();
			}
		}
		
		private void GroupRemoved(string address)
		{
			if (FGroups.ContainsKey(address))
			{
				var group = FGroups[address];
				//FLogger.Log(LogType.Debug, "group removed: " + group.Label);
				
				//move all params of the group to the root group
				var paramsOfGroup = GetGroupParameters(address);
				foreach (var param in paramsOfGroup)
				{
					//FLogger.Log(LogType.Debug, "to root: " + param.Label);
					FRCPServer.AddParameter(param, null);
				}	
				
				//then remove the actual group-param
				FRCPServer.RemoveParameter(group);
				FGroups.Remove(address);
								
				FRCPServer.Update();
			}
		}
		
		private IEnumerable<IParameter> GetGroupParameters(string address)
		{
			var paramIds = FCachedParams.Values.Select(p => p.Id);
			foreach (var id in paramIds)
			{
				var param = FRCPServer.GetParameter(id);
				
				var lastSlash = param.UserId.LastIndexOf('/');
				var temp = param.UserId.Substring(0, lastSlash);
				lastSlash = temp.LastIndexOf('/');
				var parentPath = param.UserId.Substring(0, lastSlash);
				
				//FLogger.Log(LogType.Debug, "temp: " + temp);
				if (parentPath == address)
					yield return param;
			}
		}
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			if (FTransporter == null)
			{
				FTransporter = new WebsocketServerTransporter(FHost[0], FPort[0]);
				FRCPServer.AddTransporter(FTransporter);
			}
			
			if (FHost.IsChanged || FPort.IsChanged)
				FTransporter.Bind(FHost[0], FPort[0]);
			
			//TODO: subscribe to enum-changes on the host and update all related
			//parameters as changes happen, so a client can update its gui accordingly
			if (FUpdateEnums[0])
			{
				var enumPins = FCachedPins.Values.Where(v => v.Type == "Enumeration");
				
				foreach (var enumPin in enumPins)
					PinValueChanged(enumPin, null);
			}

			//process FParameterQueue
			//in order to handle all messages from main thread
			//since all COM-access is single threaded
			lock(FParameterQueue)
			{
				foreach (var param in FParameterQueue)
				{
					IPin2 pin;
//					if (FParameterQueue.Any())
//						FLogger.Log(LogType.Debug, "updates: " + FParameterQueue.Count.ToString());
					if (FCachedPins.TryGetValue(param.UserId, out pin))
						{
//							pin.Spread = RCP.Helpers.ValueToString(param);
							pin.Spread = RCP.Helpers.Float32ToString(((IValueParameter<float>)param).Value);
//							FLogger.Log(LogType.Debug, "userid: " + param.UserId);
//							FLogger.Log(LogType.Debug, "spread: " + pin.Spread);
						}
				}
				FParameterQueue.Clear();
			}
			
			FConnectionCount[0] = FTransporter.ConnectionCount;
		}
		
		private void NodeAddedCB(INode2 node)
		{
			var pinName = PinNameFromNode(node);
			var pin = node.FindPin(pinName);
			pin.Changed += PinValueChanged;
			node.LabelPin.Changed += LabelChanged;
			var tagPin = node.FindPin("Tag");
			tagPin.Changed += TagChanged;
			//TODO: subscribe to subtype-pins here as well
			//default, min, max, ...
			
			var userId = IdFromPin(pin);
			FCachedPins.Add(userId, pin);
			
			var parentId = ParentIdFromNode(node);
			var param = ParameterFromNode(node, userId, parentId);
			param.Updated += ParameterUpdated;
			FCachedParams.Add(userId, param);
			
			//group
			var parentPath = node.Parent.GetNodePath(false);
			if (FGroups.ContainsKey(parentPath))
			{
				var group = FGroups[parentPath];	
				FRCPServer.AddParameter(param, group);
				//FLogger.Log(LogType.Debug, "added to: " + group.Label);
			}
			
			//AddParamToPatch(parentId, param);
			//OutputBytes(param);

			FRCPServer.Update();
		}
		
		private void NodeRemovedCB(INode2 node)
		{
			var pinName = PinNameFromNode(node);
			var pin = node.FindPin(pinName);
			pin.Changed -= PinValueChanged;
			node.LabelPin.Changed -= LabelChanged;
			var tagPin = node.FindPin("Tag");
			tagPin.Changed -= TagChanged;
			
			var userId = IdFromPin(pin);
			FCachedPins.Remove(userId);
			var param = FCachedParams[userId];
			param.Updated -= ParameterUpdated;
			FRCPServer.RemoveParameter(param);
			FCachedParams.Remove(userId);
//			
//			var rcpId = id.ToRCPId();
//			RemoveParamFromPatch(ParentIdFromNode(node), FRCPServer.GetParameter(rcpId));
			
			FRCPServer.Update();
		}
		
		private string IdFromPin(IPin2 pin)
		{
			var pinname = PinNameFromNode(pin.ParentNode);
			var pinpath = pin.ParentNode.GetNodePath(false) + "/" + pinname;
			return pinpath;
		}
		
		private string ParentIdFromNode(INode2 node)
		{
			var path = node.GetNodePath(false);
			var ids = path.Split('/'); 
			return string.Join("/", ids.Take(ids.Length-1));
		}
		
		private string PinNameFromNode(INode2 node)
		{ 
			string pinName = "";
			if (node.NodeInfo.Systemname == "IOBox (Value Advanced)")
			pinName = "Y Input Value";
			else if (node.NodeInfo.Systemname == "IOBox (String)")
			pinName = "Input String";
			else if (node.NodeInfo.Systemname == "IOBox (Color)")
			pinName = "Color Input";
			else if (node.NodeInfo.Systemname == "IOBox (Enumerations)")
			pinName = "Input Enum";
			else if (node.NodeInfo.Systemname == "IOBox (Node)")
			pinName = "Input Node";
			
			return pinName;
		}
		
		private IParameter ParameterFromNode(INode2 node, string userId, string parentId)
		{
			var pinName = PinNameFromNode(node);
			var pin = node.FindPin(pinName);
			var id = IdFromPin(pin);
			
			IParameter parameter = null;
			
			var subtype = pin.SubType.Split(',').Select(s => s.Trim()).ToArray();
			var sliceCount = pin.SliceCount;
			var label = pin.ParentNode.LabelPin.Spread.Trim('|');
			
			switch(pin.Type)
			{
				case "Value": 
				{
					var dimensions = int.Parse(subtype[1]);
					//figure out the actual spreadcount
					//taking dimensions (ie. vectors) of value-spreads into account
					sliceCount /= dimensions;
					
					if (dimensions == 1)
					{
						int intStep = 0;
						float floatStep = 0;
						
						if (int.TryParse(subtype[5], out intStep)) //integer
						{
	                        var isbool = (subtype[3] == "0") && (subtype[4] == "1");
	                        if (isbool)
	                        {
	                        	var def = subtype[2] == "1";
	                        	parameter = GetBoolParameter(label, sliceCount, def, pin, (p,i) => {return p[i] == "1";});
	                        }
							else
							{
								var def = RCP.Helpers.ParseInt(subtype[2]);
								var min = RCP.Helpers.ParseInt(subtype[3]);
								var max = RCP.Helpers.ParseInt(subtype[4]);
								parameter = GetNumberParameter<int>(label, sliceCount, 1, def, min, max, intStep, pin, (p,i) => RCP.Helpers.GetInt(p,i));
							}
						}
						else if (float.TryParse(subtype[5], NumberStyles.Float, CultureInfo.InvariantCulture, out floatStep))
						{
							var def = RCP.Helpers.ParseFloat(subtype[2]);
							var min	= RCP.Helpers.ParseFloat(subtype[3]);
							var max = RCP.Helpers.ParseFloat(subtype[4]);
							parameter = GetNumberParameter<float>(label, sliceCount, 1, def, min, max, floatStep, pin, (p,i) => RCP.Helpers.GetFloat(p,i));
						}
						
						switch (subtype[0])
						{
							case "Bang": parameter.Widget = new BangWidget(); break;
							case "Press": parameter.Widget = new PressWidget(); break;
							case "Toggle": parameter.Widget = new ToggleWidget(); break;
							case "Slider": parameter.Widget = new SliderWidget(); break;
							case "Endless": parameter.Widget = new NumberboxWidget(); break;
						}
						
						//widget display precision
						//int.TryParse(subtype[7], out precision);
					}
					else if (dimensions == 2)
					{
						//TODO: parse 2d subtype when pin.Subtype supports it
						//var comps = subtype[2].Split(',');
						//FLogger.Log(LogType.Debug, subtype[2]);
						var def = new V2(RCP.Helpers.ParseFloat(subtype[2]));
						var min = new V2(RCP.Helpers.ParseFloat(subtype[3]));
						var max = new V2(RCP.Helpers.ParseFloat(subtype[4]));
						var multipleOf = new V2(RCP.Helpers.ParseFloat(subtype[5]));
						parameter = GetNumberParameter<V2>(label, sliceCount, 2, def, min, max, multipleOf, pin, (p,i) => RCP.Helpers.GetVector2(p,i));
					}
					else if (dimensions == 3)
					{
						//TODO: parse 3d subtype when pin.Subtype supports it
						//var comps = subtype[2].Split(',');
						//FLogger.Log(LogType.Debug, subtype[2]);
						var def = new V3(RCP.Helpers.ParseFloat(subtype[2]));
						var min = new V3(RCP.Helpers.ParseFloat(subtype[3]));
						var max = new V3(RCP.Helpers.ParseFloat(subtype[4]));
						var multipleOf = new V3(RCP.Helpers.ParseFloat(subtype[5]));
						parameter = GetNumberParameter<V3>(label, sliceCount, 3, def, min, max, multipleOf, pin, (p,i) => RCP.Helpers.GetVector3(p,i));
					}
					else if (dimensions == 4)
					{
						//TODO: parse 3d subtype when pin.Subtype supports it
						//var comps = subtype[2].Split(',');
						//FLogger.Log(LogType.Debug, subtype[2]);
						var def = new V4(RCP.Helpers.ParseFloat(subtype[2]));
						var min = new V4(RCP.Helpers.ParseFloat(subtype[3]));
						var max = new V4(RCP.Helpers.ParseFloat(subtype[4]));
						var multipleOf = new V4(RCP.Helpers.ParseFloat(subtype[5]));
						parameter = GetNumberParameter<V4>(label, sliceCount, 4, def, min, max, multipleOf, pin, (p,i) => RCP.Helpers.GetVector4(p,i));
					}
					break;
				}
				
				case "String": 
				{
					var s = subtype[0].ToLower();
					var def = subtype[1];
					if (s == "filename" || s == "directory")
					{
						var schema = "file";
						var filter = "";
						if (s == "filename")
							filter = subtype[2];
						
						//var v = pin[0].TrimEnd('\\').Replace("\\", "/");
//						if (schema == "directory")
//							v += "/";
						
						parameter = GetUriParameter(label, sliceCount, def, schema, filter, pin, (p,i) => p[i]);
					}
					else if (s == "url")
					{
						var schema = "http";
						parameter = GetUriParameter(label, sliceCount, def, schema, "", pin, (p,i) => p[i]);
					}
					else 
					{
						parameter = GetStringParameter(label, sliceCount, def, pin, (p,i) => p[i]);
					}
					
					break;
				}
				case "Color":
	            {
		            /// colors: guiType, default, hasAlpha
	                bool hasAlpha = subtype[2].Trim() == "HasAlpha";
	            	//TODO: implement default for color IOBoxes
	            	var def = Color.Red;
	            	parameter = GetRGBAParameter(label, sliceCount, def, pin, (p,i) => RCP.Helpers.ParseColor(pin[i]));
	            	break;
	            }
				case "Enumeration":
	            {
		            /// enums: guiType, enumName, default
	                var enumName = subtype[1].Trim();
	            	var def = subtype[2].Trim();
	            	parameter = GetEnumParameter(label, sliceCount, enumName, def, pin, (p,i) => p[i]);
	            	break;
	            }
			}
			
			//no suitable parameter found?
			if (parameter == null)
			{
				parameter = FRCPServer.CreateStringParameter("Unknown Type");
				parameter.Description = label;
			}

			//FLogger.Log(LogType.Debug, address + " - " + ParentMap.GetName(address));
			
			//order
			var bounds = node.GetBounds(BoundsType.Box);
			parameter.Order = bounds.X;
			
			//userid
			parameter.UserId = userId;
			
			
			
			//userdata
			var tag = node.FindPin("Tag");
            if (tag != null)
                parameter.Userdata = Encoding.UTF8.GetBytes(tag.Spread.Trim('|'));
			
			return parameter;
		}
		
		private void ParameterUpdated(object sender, EventArgs e)
        {
        	IPin2 pin;
        	if (FCachedPins.TryGetValue((sender as IParameter).UserId, out pin))
        	{
				pin.Spread = RCP.Helpers.ValueToString(sender as IParameter);
        		FLogger.Log(LogType.Debug, "remote: " + pin.Spread);
        	}
        	
//            lock(FParameterQueue)
//				FParameterQueue.Add(sender as IParameter);
        }
		
		
		private EnumDefinition GetEnumDefinition(string enumName, string deflt)
		{
			var entryCount = EnumManager.GetEnumEntryCount(enumName);
            var entries = new List<string>();
            for (int i = 0; i < entryCount; i++)
                entries.Add(EnumManager.GetEnumEntryString(enumName, i));

            var def = new EnumDefinition();
            def.Default = deflt; //(ushort) entries.IndexOf(deflt);
        	def.Entries = entries.ToArray();
			
			return def;
		}
		
		private void AddParamToPatch(string address, IParameter param)
		{
			if (!FParameters.ContainsKey(address))
				FParameters.Add(address, new List<IParameter>());
			
			FParameters[address].Add(param);
		}
		
		private void RemoveParamFromPatch(string address, IParameter param)
		{
			FParameters[address].Remove(param);
			if (FParameters[address].Count == 0)
				FParameters.Remove(address);
		}
		
		private IParameter GetBoolParameter(string label, int sliceCount, bool def, IPin2 pin, Func<IPin2, int, bool> parse)
		{
			if (sliceCount == 1)
			{
				var param = FRCPServer.CreateBooleanParameter(label);
				param.Default = def;
				param.Value = parse(pin, 0);
				return param;
			}
			else
			{
				var param = FRCPServer.CreateBooleanArrayParameter(label, sliceCount);
				var values = new List<bool>();
				var defs = new List<bool>();
				for (int i=0; i<sliceCount; i++)
				{
					values.Add(parse(pin, i));
					defs.Add(def);
				}
				param.Value = values.ToArray(); 
				param.Default = defs.ToArray();
				return param;
			}
		}
		
		private IParameter GetNumberParameter<T>(string label, int sliceCount, int dimensions, T def, T min, T max, T multiple, IPin2 pin, Func<IPin2, int, T> parse) where T: struct
		{
			if (sliceCount == 1)
			{
				var param = FRCPServer.CreateNumberParameter<T>(label);
				param.Default = def;
				param.Minimum = min;
				param.Maximum = max;
				param.MultipleOf = multiple;
				param.Value = parse(pin, 0);
				return param;
			}
			else
			{
				var param = FRCPServer.CreateNumberArrayParameter<T[], T>(label, sliceCount);
				var values = new List<T>();
				var defs = new List<T>();
				//TODO:set multiple, min, max
//				var mins = new List<T>();
//				var maxs = new List<T>();
//				var mults = new List<T>();
				for (int i=0; i<sliceCount; i++)
				{
					values.Add(parse(pin, i*dimensions));
					defs.Add(def);					
				}
				param.Value = values.ToArray();
				param.Default = defs.ToArray();
				return param;
			}
		}
			
		private IParameter GetStringParameter(string label, int sliceCount, string def, IPin2 pin, Func<IPin2, int, string> parse)
		{
			if (sliceCount == 1)
			{
				var param = FRCPServer.CreateStringParameter(label);
				param.Default = def;
				param.Value = parse(pin, 0);
				return param;
			}
			else
			{
				var param = FRCPServer.CreateStringArrayParameter(label, sliceCount);
				var values = new List<string>();
				var defs = new List<string>();
				for (int i=0; i<sliceCount; i++)
				{
					values.Add(parse(pin, i));
					defs.Add(def);
				}
				param.Value = values.ToArray(); 
				param.Default = defs.ToArray();
				return param;
			}
		}
		
		private IParameter GetUriParameter(string label, int sliceCount, string def, string schema, string filter, IPin2 pin, Func<IPin2, int, string> parse)
		{
			if (sliceCount == 1)
			{
				var param = FRCPServer.CreateUriParameter(label);
				param.Default = def;
				param.Schema = schema;
				param.Filter = filter;
				param.Value = parse(pin, 0);
				return param;
			}
			else
			{
				var param = FRCPServer.CreateUriArrayParameter(label, sliceCount);
				//TODO:set schema, filter
				var values = new List<string>();
				var defs = new List<string>();
				for (int i=0; i<sliceCount; i++)
				{
					values.Add(parse(pin, i));
					defs.Add(def);
				}	
				param.Value = values.ToArray();
				param.Default = defs.ToArray();
				return param;
			}
		}
		
		private IParameter GetRGBAParameter(string label, int sliceCount, Color def, IPin2 pin, Func<IPin2, int, Color> parse)
		{
			if (sliceCount == 1)
			{
				var param = FRCPServer.CreateRGBAParameter(label);
				param.Default = def;
				param.Value = parse(pin, 0);
				return param;
			}
			else
			{
				var param = FRCPServer.CreateRGBAArrayParameter(label, sliceCount);
				var values = new List<Color>();
				var defs = new List<Color>();
				for (int i=0; i<sliceCount; i++)
				{
					values.Add(parse(pin, i));
					defs.Add(def);
				}
				param.Value = values.ToArray(); 
				param.Default = defs.ToArray(); 
				return param;
			}
		}
		
		private IParameter GetEnumParameter(string label, int sliceCount, string name, string def, IPin2 pin, Func<IPin2, int, string> parse)
		{
			var definition = GetEnumDefinition(name, def);
			if (sliceCount == 1)
			{
				var param = FRCPServer.CreateEnumParameter(label);
				param.Default = def;
				param.Entries = definition.Entries;
				param.Value = parse(pin, 0);
				return param;
			}
			else
			{
				var param = FRCPServer.CreateEnumArrayParameter(label, sliceCount);
				//TODO:set entries
				var values = new List<string>();
				var defs = new List<string>();
				for (int i=0; i<sliceCount; i++)
				{
					values.Add(parse(pin, i));
					defs.Add(def);
				}
				param.Value = values.ToArray(); 
				param.Default = defs.ToArray();
				return param;
			}
		}
		
		private void LabelChanged(object sender, EventArgs e)
		{
			var labelPin = sender as IPin2;
			var userId = IdFromPin(labelPin);
			
			FCachedParams[userId].Label = labelPin.Spread.Trim('|');
			FRCPServer.Update();
		}
		
		private void TagChanged(object sender, EventArgs e)
		{
			var tagPin = sender as IPin2;
			var userId = IdFromPin(tagPin);
			
			FCachedParams[userId].Userdata = Encoding.UTF8.GetBytes(tagPin.Spread.Trim('|'));
			FRCPServer.Update();
		}
		
		//the application updated a value
		private void PinValueChanged(object sender, EventArgs e)
		{
			//here it coult make sense to think about a
			//beginframe/endframe bracket to not send every changed pin directly
			//but collect them and send them per frame in a bundle
			var pin = sender as IPin2;
			var userId = IdFromPin(pin);
			
			FLogger.Log(LogType.Debug, "id: " + userId);
			var param = FCachedParams[userId];
			//in case of enum pin we also update the full definition here
			//which may have changed in the meantime
			//TODO: subscribe to enum-changes on the host and update all related
			//parameters as changes happen, so a client can update its gui accordingly
			if (pin.Type == "Enumeration")
			{
				var subtype = pin.SubType.Split(',').Select(s => s.Trim()).ToArray();
				var enumName = subtype[1].Trim();
				var dflt = subtype[2].Trim();
				var newDef = GetEnumDefinition(enumName, dflt);
				IEnumDefinition paramDef;
				if (pin.SliceCount == 1)
					paramDef = param.TypeDefinition as IEnumDefinition;
				else
					paramDef = (param.TypeDefinition as IArrayDefinition).ElementDefinition as IEnumDefinition;
				paramDef.Default = newDef.Default;
				paramDef.Entries = newDef.Entries;
				//FLogger.Log(LogType.Debug, "count: " + pin.Spread);
			}
			RCP.Helpers.StringToValue(param, pin.Spread);
			
			FRCPServer.Update();
			
			//OutputBytes(param);
		}
		
		private void OutputBytes(IParameter param)
		{
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream))
			{
				param.Write(writer);
				//FOutput.AssignFrom(stream.ToArray());
			}
		}
		
		//an RCP client has updated a value
//		private void ParameterUpdated(IParameter parameter)
//		{
//			lock(FParameterQueue)
//				FParameterQueue.Add(parameter);
//		}
	}
}

namespace RCP
{
	public static class Helpers
	{
		//vvvv string/enum escaping rules:
		//if a slice contains either a space " ", a pipe "|" or a comma ","
		//the slice is quoted with pipes "|like so|"
		//and also every pipe is escaped with another pipe "|like||so|" to encode a string like "like|so"
		
		private static string PipeEscape(string input)
		{
			if (input.Contains(",") || input.Contains("|") || input.Contains(" "))
			{
				input = input.Replace("|", "||");
				input = "|" + input + "|";
			}
			return input;
		}
		
		public static string ValueToString(IParameter param)
		{
			try
			{
				switch (param.TypeDefinition.Datatype)
				{
					case RcpTypes.Datatype.Boolean: return RCP.Helpers.BoolToString((param as IBooleanParameter).Value);
					case RcpTypes.Datatype.String: return PipeEscape((param as IStringParameter).Value);
					case RcpTypes.Datatype.Uri: return PipeEscape((param as IUriParameter).Value);
					case RcpTypes.Datatype.Enum: return PipeEscape((param as IEnumParameter).Value);
					case RcpTypes.Datatype.Float32: return RCP.Helpers.Float32ToString((param as INumberParameter<float>).Value);
					case RcpTypes.Datatype.Int32: return RCP.Helpers.Int32ToString((param as INumberParameter<int>).Value);
					case RcpTypes.Datatype.Vector2f32: return RCP.Helpers.Vector2f32ToString((param as INumberParameter<Vector2>).Value);
					case RcpTypes.Datatype.Vector3f32: return RCP.Helpers.Vector3f32ToString((param as INumberParameter<Vector3>).Value);
					case RcpTypes.Datatype.Vector4f32: return RCP.Helpers.Vector4f32ToString((param as INumberParameter<Vector4>).Value);
					case RcpTypes.Datatype.Rgba: return RCP.Helpers.ColorToString((param as IRGBAParameter).Value);
					case RcpTypes.Datatype.Group: return "";
					case RcpTypes.Datatype.Array:
					{
						switch ((param.TypeDefinition as IArrayDefinition).ElementType)
						{
							case RcpTypes.Datatype.Boolean:
							{
								var val = ((IBooleanArrayParameter)param).Value;
								return string.Join(",", val.Select(v => BoolToString(v)));
							}
							case RcpTypes.Datatype.Enum:
							{
								//TODO; accessing the subtypes entries fails
								var val = ((IEnumArrayParameter)param).Value;
								return string.Join(",", val.Select(v => PipeEscape(v)));
							}						
							case RcpTypes.Datatype.Int32:
							{
								var val = ((INumberArrayParameter<int[], int>)param).Value;
								return string.Join(",", val.Select(v => Int32ToString(v)));
							}
							case RcpTypes.Datatype.Float32:
							{
								var val = ((INumberArrayParameter<float[], float>)param).Value;
								return string.Join(",", val.Select(v => Float32ToString(v)));
							}
							case RcpTypes.Datatype.Vector2f32:
							{
								var val = ((INumberArrayParameter<V2[], V2>)param).Value;
								return string.Join(",", val.Select(v => Vector2f32ToString(v)));
							}		
							case RcpTypes.Datatype.Vector3f32:
							{
								var val = ((INumberArrayParameter<V3[], V3>)param).Value;
								return string.Join(",", val.Select(v => Vector3f32ToString(v)));
							}	
							case RcpTypes.Datatype.Vector4f32:
							{
								var val = ((INumberArrayParameter<V4[], V4>)param).Value;
								return string.Join(",", val.Select(v => Vector4f32ToString(v)));
							}	
							case RcpTypes.Datatype.String:
							{
								var val = ((IStringArrayParameter)param).Value;
								return string.Join(",", val.Select(v => PipeEscape(v)));
							}
							case RcpTypes.Datatype.Uri:
							{
								var val = ((IUriArrayParameter)param).Value;
								return string.Join(",", val.Select(v => PipeEscape(v)));
							}
							case RcpTypes.Datatype.Rgba:
							{
								var val = ((IRGBAArrayParameter)param).Value;
								return string.Join(",", val.Select(v => ColorToString(v)));
							}
							
							default: return ""; //param.Value.ToString();
						}
					}
					default: return "null";
				}
			}
			catch (Exception e)
			{
				return e.Message;
			}
		}
		
		public static string PipeUnEscape(string input)
		{
			if (string.IsNullOrEmpty(input))
				return input;
			
			if (input[0] == '|' && input[input.Length-1] == '|')
				input = input.Substring(1, input.Length-2);
			return input.Replace("||", "|");
		}
		
		//sets the value given as string on the given parameter
		public static IParameter StringToValue(IParameter param, string input)
		{
			try
			{
				switch(param.TypeDefinition.Datatype)
				{
					case RcpTypes.Datatype.Boolean:
					{
						var p = (IBooleanParameter)param;
						p.Value = ParseBool(input);
						return p;
					}
					case RcpTypes.Datatype.Enum:
					{
						var p = (IEnumParameter)param;
						p.Value = PipeUnEscape(input);
						return p;
					}
					case RcpTypes.Datatype.Int32:
					{
						var p = (INumberParameter<int>)param;
						p.Value = ParseInt(input);
						return p;
					}
					case RcpTypes.Datatype.Float32:
					{
						var p = (INumberParameter<float>)param;
						p.Value = ParseFloat(input);
						return p;
					}
					case RcpTypes.Datatype.String:
					{
						var p = (IStringParameter)param;
						p.Value = PipeUnEscape(input);
						return p;
					}
					case RcpTypes.Datatype.Uri:
					{
						var p = (IUriParameter)param;
						p.Value = PipeUnEscape(input);
						return p;
					}
					case RcpTypes.Datatype.Rgba:
					{
						var p = (IRGBAParameter)param;
						p.Value = ParseColor(input);
						return p;
					}
					case RcpTypes.Datatype.Vector2f32:
					{
						var p = (INumberParameter<V2>)param;
						p.Value = ParseVector2(input);
						return p;
					}
					case RcpTypes.Datatype.Vector3f32:
					{
						var p = (INumberParameter<V3>)param;
						p.Value = ParseVector3(input);
						return p;
					}
					case RcpTypes.Datatype.Vector4f32:
					{
						var p = (INumberParameter<V4>)param;
						p.Value = ParseVector4(input);
						return p;
					}
					case RcpTypes.Datatype.Array:
					{
						switch ((param.TypeDefinition as IArrayDefinition).ElementType)
						{
							case RcpTypes.Datatype.Boolean:
							{
								var p = (IBooleanArrayParameter)param;
								p.Value = input.Split(',').Select(s => ParseBool(s)).ToArray();
								return p;
							}
							case RcpTypes.Datatype.Enum:
							{
								var p = (IEnumArrayParameter)param;
								p.Value = SplitToSlices(input).Select(s => PipeUnEscape(s)).ToArray();
								return p;
							}
							case RcpTypes.Datatype.Int32:
							{
								var p = (INumberArrayParameter<int[], int>)param;
								p.Value = input.Split(',').Select(s => ParseInt(s)).ToArray();
								return p;
							}
							case RcpTypes.Datatype.String:
							{
								var p = (IStringArrayParameter)param;
								p.Value = SplitToSlices(input).Select(s => PipeUnEscape(s)).ToArray();
								return p;
							}
							case RcpTypes.Datatype.Uri:
							{
								var p = (IUriArrayParameter)param;
								p.Value = SplitToSlices(input).Select(s => PipeUnEscape(s)).ToArray();
								return p;
							}
							case RcpTypes.Datatype.Float32:
							{
								var p = (INumberArrayParameter<float[], float>)param;
								p.Value = input.Split(',').Select(s => ParseFloat(s)).ToArray();
								return p;
							}
							case RcpTypes.Datatype.Vector2f32:
							{
								var p = (INumberArrayParameter<V2[], V2>)param;
								var v = input.Split(',');
								for (int i=0; i<v.Count()/2; i++)
									p.Value[i] = new V2(ParseFloat(v[i*2]), ParseFloat(v[i*2+1]));
								return p;
							}
							case RcpTypes.Datatype.Vector3f32:
							{
								var p = (INumberArrayParameter<V3[], V3>)param;
								var v = input.Split(',');
								for (int i=0; i<v.Count()/3; i++)
									p.Value[i] = new V3(ParseFloat(v[i*3]), ParseFloat(v[i*3+1]), ParseFloat(v[i*3+2]));
								return p;
							}
							case RcpTypes.Datatype.Vector4f32:
							{
								var p = (INumberArrayParameter<V4[], V4>)param;
								var v = input.Split(',');
								for (int i=0; i<v.Count()/4; i++)
									p.Value[i] = new V4(ParseFloat(v[i*4]), ParseFloat(v[i*4+1]), ParseFloat(v[i*4+2]), ParseFloat(v[i*4+3]));
								return p;
							}
							case RcpTypes.Datatype.Rgba:
							{
								var p = (IRGBAArrayParameter)param;
								//split at commas outside of pipes
								p.Value = SplitToSlices(input).Select(s => ParseColor(s)).ToArray();
								return p;
							}
						}
						break;
					}
				}
			}
			catch
			{
				//string parsing went wrong...						
			}
			
			return param;
		}
		
		private static List<string> SplitToSlices(string input)
		{
			return Regex.Split(input, @",(?=(?:[^\|]*\|[^\|]*\|)*[^\|]*$)").ToList();
		}
		
		public static V2 GetVector2(IPin2 pin, int index)
		{
			var x = ParseFloat(pin[index]);
			var y = ParseFloat(pin[index+1]);
			return new V2(x, y);
		}
		
		public static V2 ParseVector2(string input)
		{
			var comps = input.Split(',');
			return new V2(ParseFloat(comps[0]), ParseFloat(comps[1]));
		}
		
		public static V3 GetVector3(IPin2 pin, int index)
		{
			var x = ParseFloat(pin[index]);
			var y = ParseFloat(pin[index+1]);
			var z = ParseFloat(pin[index+2]);
			return new V3(x, y, z);
		}
		
		public static V3 ParseVector3(string input)
		{
			var comps = input.Split(',');
			return new V3(ParseFloat(comps[0]), ParseFloat(comps[1]), ParseFloat(comps[2]));
		}
		
		public static V4 GetVector4(IPin2 pin, int index)
		{
			var x = ParseFloat(pin[index]);
			var y = ParseFloat(pin[index+1]);
			var z = ParseFloat(pin[index+2]);
			var w = ParseFloat(pin[index+3]);
			return new V4(x, y, z, w);
		}
		
		public static V4 ParseVector4(string input)
		{
			var comps = input.Split(',');
			return new V4(ParseFloat(comps[0]), ParseFloat(comps[1]), ParseFloat(comps[2]), ParseFloat(comps[3]));
		}
		
		public static bool ParseBool(string input)
		{
			return input == "1" ? true : false;
		}
		
		public static ushort ParseEnum(string input, string[] entries)
		{
			return (ushort)entries.ToList().IndexOf(input);
		}
		
		public static float GetFloat(IPin2 pin, int index)
		{
			return ParseFloat(pin[index]);
		}
		
		public static float ParseFloat(string input)
		{
			float v;
			float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
			return v;
		}
		
		public static int GetInt(IPin2 pin, int index)
		{
			return ParseInt(pin[index]);
		}
		
		public static int ParseInt(string input)
		{
			int v;
			int.TryParse(input, out v);
			return v;
		}
		
		public static Color GetColor(IPin2 pin, int index)
		{
			return ParseColor(pin[index]);
		}
		
		public static Color ParseColor(string input)
		{
			var comps = input.Trim('|').Split(',');
	        var r = 255 * float.Parse(comps[0], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var g = 255 * float.Parse(comps[1], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var b = 255 * float.Parse(comps[2], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var a = 255 * float.Parse(comps[3], NumberStyles.Float, CultureInfo.InvariantCulture);
	        var color = Color.FromArgb((int)a, (int)r, (int)g, (int)b);
			return color;
		}
		
		public static string ColorToString(Color input)
		{
			return "|" + Float32ToString(input.R / 255f) + "," + Float32ToString(input.G / 255f) + "," + Float32ToString(input.B / 255f) + "," + Float32ToString(input.A / 255f) + "|";
		}
		
		public static string BoolToString(bool input)
		{
			return input ? "1" : "0";
		}
		
		public static string Float32ToString(float input)
		{
			return input.ToString(CultureInfo.InvariantCulture);
		}
		
		public static string Int32ToString(int input)
		{
			return input.ToString(CultureInfo.InvariantCulture);
		}
		
		public static string EnumToString(ushort input, string[] entries)
		{
			if (input >= 0 && input < entries.Length)
				return entries[input];
			else
				return "";
		}
		
		public static string Vector2f32ToString(V2 input)
		{
			return Float32ToString(input.X) + "," + Float32ToString(input.Y);
		}
		
		public static string Vector3f32ToString(V3 input)
		{
			return Float32ToString(input.X) + "," + Float32ToString(input.Y) + "," + Float32ToString(input.Z);
		}
		
		public static string Vector4f32ToString(V4 input)
		{
			return Float32ToString(input.X) + "," + Float32ToString(input.Y) + "," + Float32ToString(input.Z)+ "," + Float32ToString(input.W);
		}
		
		public static string DatatypeToString(ITypeDefinition definition)
		{
			var result = "";
			if (definition.Datatype == RcpTypes.Datatype.Array)
			{
				var def = definition as IArrayDefinition;
				result = "Array<" + def.ElementType.ToString() + ">";
			}
			else
				result = definition.Datatype.ToString();
			
			return result;
		}
		
		public static string TypeDefinitionToString(ITypeDefinition definition)
		{
			try
			{
				switch(definition.Datatype)
				{
					case RcpTypes.Datatype.Boolean:
					{
						var def = (IBoolDefinition)definition;
						return def.Default ? "1" : "0";
					}
					case RcpTypes.Datatype.Enum:
					{
						var def = (IEnumDefinition)definition;
						return def.Default; //, ((IEnumDefinition)def).Entries) + ", [" + string.Join(",", def.Entries) + "]";
					}
					case RcpTypes.Datatype.Int32:
					{
						var def = (INumberDefinition<int>)definition;
						return Int32ToString(def.Default) + ", " + Int32ToString((int)def.Minimum) + ", " + Int32ToString((int)def.Maximum) + ", " + Int32ToString((int)def.MultipleOf);
					}
					case RcpTypes.Datatype.Float32:
					{
						var def = (INumberDefinition<float>)definition;
						return Float32ToString(def.Default) + ", " + Float32ToString((float)def.Minimum) + ", " + Float32ToString((float)def.Maximum) + ", " + Float32ToString((float)def.MultipleOf);
					}
					case RcpTypes.Datatype.Vector2f32:
					{
						var def = (INumberDefinition<V2>)definition;
						return Vector2f32ToString(def.Default) + ", " + Vector2f32ToString((V2)def.Minimum) + ", " + Vector2f32ToString((V2)def.Maximum) + ", " + Vector2f32ToString((V2)def.MultipleOf);
					}
					case RcpTypes.Datatype.Vector3f32:
					{
						var def = (INumberDefinition<V3>)definition;
						return Vector3f32ToString(def.Default) + ", " + Vector3f32ToString((V3)def.Minimum) + ", " + Vector3f32ToString((V3)def.Maximum) + ", " + Vector3f32ToString((V3)def.MultipleOf);
					}
					case RcpTypes.Datatype.Vector4f32:
					{
						var def = (INumberDefinition<V4>)definition;
						return Vector4f32ToString(def.Default) + ", " + Vector4f32ToString((V4)def.Minimum) + ", " + Vector4f32ToString((V4)def.Maximum) + ", " + Vector4f32ToString((V4)def.MultipleOf);
					}
					case RcpTypes.Datatype.String:
					{
						var def = (IStringDefinition)definition;
						return def.Default;
					}
					case RcpTypes.Datatype.Uri:
					{
						var def = (IUriDefinition)definition;
						return def.Default + ", " + def.Schema + ", " + def.Filter;
					}
					case RcpTypes.Datatype.Rgba:
					{
						var def = (IRGBADefinition)definition;
						return ColorToString(def.Default);
					}
					case RcpTypes.Datatype.Array:
					{
						var def = definition as IArrayDefinition;
						switch(def.ElementType)
						{
							case RcpTypes.Datatype.Boolean: return TypeDefinitionToString((IBoolDefinition)def.ElementDefinition);
							case RcpTypes.Datatype.Float32: return TypeDefinitionToString((INumberDefinition<float>)def.ElementDefinition);
							case RcpTypes.Datatype.Int32: return TypeDefinitionToString((INumberDefinition<int>)def.ElementDefinition);
							case RcpTypes.Datatype.Vector2f32: return TypeDefinitionToString((INumberDefinition<V2>)def.ElementDefinition);
							case RcpTypes.Datatype.Vector3f32: return TypeDefinitionToString((INumberDefinition<V3>)def.ElementDefinition);
							case RcpTypes.Datatype.Vector4f32: return TypeDefinitionToString((INumberDefinition<V4>)def.ElementDefinition);
							case RcpTypes.Datatype.String: return TypeDefinitionToString((IStringDefinition)def.ElementDefinition);
							case RcpTypes.Datatype.Uri: return TypeDefinitionToString((IUriDefinition)def.ElementDefinition);
							case RcpTypes.Datatype.Rgba: return TypeDefinitionToString((IRGBADefinition)def.ElementDefinition);
							case RcpTypes.Datatype.Enum: return TypeDefinitionToString((IEnumDefinition)def.ElementDefinition);
							
							default: return "Unknown Type";
						}
					}
					case RcpTypes.Datatype.Group: return "";
					
					default: return "Unknown Type";
				}
			}
			catch (Exception e)
			{
				return e.Message;
			}
		}
	}
}