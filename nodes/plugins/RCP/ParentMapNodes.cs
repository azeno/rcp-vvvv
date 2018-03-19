#region usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel.Composition;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;
#endregion usings

namespace VVVV.Nodes
{
	public static class GroupMap
	{
		//address -> unique id
		static Dictionary<string, string> FMap = new Dictionary<string, string>();
		
		public static Action<string> GroupChanged;
		
		public static string GetName(string address)
		{
			if (FMap.ContainsKey(address))
				return FMap[address];
			else
				return "";			
		}
		
		public static string GetAddress(string name)
		{
			return FMap.FirstOrDefault(x => x.Value == name).Key ?? "";	
		}
				
		public static void AddEntry(string address, string name)
		{
			if (!FMap.ContainsKey(address))
			{
				FMap.Add(address, name);
				GroupChanged?.Invoke(address);
			}
		}
		
		public static void RemoveEntry(string address)
		{
			if (FMap.ContainsKey(address))
			{
				FMap.Remove(address);
				GroupChanged?.Invoke(address);
			}
		}
		
		public static IEnumerable<Tuple<string, string>> GetEntries()
		{
			foreach (var key in FMap.Keys)
				yield return new Tuple<string, string>(key, FMap[key]);
		}
	}
	
	#region PluginInfo
	[PluginInfo(Name = "Group", Category = "RCP", AutoEvaluate=true)]
	#endregion PluginInfo
	public class GroupNode : IPluginEvaluate, IDisposable
	{
		#region fields & pins
		[Input("Unique Name", IsSingle = true)]
		public IDiffSpread<string> FUID;
		
		[Output("Output")]
		public ISpread<string> FOutput;

		[Import()]
		public IPluginHost FPluginHost;
		
		private string FLastAddress;
		#endregion fields & pins

		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			if (FUID.IsChanged)
			{
				RemoveUID();
				
				FOutput.SliceCount = 1;

				if (!string.IsNullOrWhiteSpace(FUID[0]))
				{
					string path;
					FPluginHost.GetNodePath(false, out path);
					var ids = path.Split('/'); 
					var address = string.Join("/", ids.Take(ids.Length-1));
					
					FLastAddress = address;
					GroupMap.AddEntry(address, FUID[0]);
					FOutput[0] = address;
				}
			}
			
			//FLogger.Log(LogType.Debug, "Logging to Renderer (TTY)");
		}
		
		private void RemoveUID()
		{
			if (!string.IsNullOrWhiteSpace(FLastAddress))
				GroupMap.RemoveEntry(FLastAddress);
		}
		
		public void Dispose()
		{
		  	RemoveUID();
		}
	}
	
	#region PluginInfo
	[PluginInfo(Name = "GroupMap", Category = "RCP")]
	#endregion PluginInfo
	public class GroupMapNode : IPluginEvaluate
	{
		#region fields & pins
		[Output("Output")]
		public ISpread<string> FOutput;

		[Import()]
		public IPluginHost FPluginHost;
		#endregion fields & pins

		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			var map = GroupMap.GetEntries();
			FOutput.SliceCount = map.Count();

			int i = 0;
			foreach (var entry in map)
			{
				FOutput[i] = entry.Item1 + " " + entry.Item2;
				i++;
			}	
		}
	}
	
//	#region PluginInfo
//	[PluginInfo(Name = "NameToID", Category = "VVVV")]
//	#endregion PluginInfo
//	public class NameToIDNode : IPluginEvaluate
//	{
//		#region fields & pins
//		[Input("Input")]
//		public ISpread<string> FInput;
//		
//		[Output("Output")]
//		public ISpread<string> FOutput;
//
//		[Import()]
//		public IPluginHost FPluginHost;
//		#endregion fields & pins
//
//		//called when data for any output pin is requested
//		public void Evaluate(int SpreadMax)
//		{
//			FOutput.SliceCount = SpreadMax;
//			
//			for (int i=0; i<SpreadMax; i++)
//			{
//				//incoming pin address with UID name part front
//				var ids = FInput[i].Split('/'); 
//				//get the address of the pin's node's parent
//				var uid = ids[0];
//				FOutput[i] = ParentMap.GetName(uid) + "/" + string.Join("/", ids.Skip(1));
//			}
//				
//		}
//	}
//	
//	#region PluginInfo
//	[PluginInfo(Name = "IDToName", Category = "VVVV")]
//	#endregion PluginInfo
//	public class IDToNameNode : IPluginEvaluate
//	{
//		#region fields & pins
//		[Input("Input")]
//		public ISpread<string> FInput;
//		
//		[Output("Output")]
//		public ISpread<string> FOutput;
//
//		[Import()]
//		public IPluginHost FPluginHost;
//		#endregion fields & pins
//
//		//called when data for any output pin is requested
//		public void Evaluate(int SpreadMax)
//		{
//			FOutput.SliceCount = SpreadMax;
//			
//			var keys = ParentMap.Map.Keys.ToList();
//			var values = ParentMap.Map.Values.ToList();
//			for (int i=0; i<SpreadMax; i++)
//			{
//				//incoming full pin address
//				var ids = FInput[i].Split('/'); 
//				//get the address of the pin's node's parent
//				var address = string.Join("/", ids.Take(ids.Length-2));
//				
//				try
//				{
//					FOutput[i] = FInput[i].Replace(address, keys[values.IndexOf(address)]);
//				}
//				catch
//				{
//					FOutput[i] = "No name found for ID: " + address;
//				}
//			}
//		}
//	}
}
