﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using ConnectorGrasshopper.Extras;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Serilog;
using Speckle.Core.Models;
using Speckle.Core.Models.Extensions;
using Logging = Speckle.Core.Logging;
using Utilities = ConnectorGrasshopper.Extras.Utilities;

namespace ConnectorGrasshopper.Objects
{
  public class ExtendSpeckleObjectTaskComponent : SelectKitTaskCapableComponentBase<Base>,
    IGH_VariableParameterComponent
  {
    protected override Bitmap Icon => Properties.Resources.ExtendSpeckleObject;
    public override GH_Exposure Exposure => GH_Exposure.secondary;
    public override Guid ComponentGuid => new Guid("2D455B11-F372-47E5-98BE-515EA758A669");

    public ExtendSpeckleObjectTaskComponent() : base("Extend Speckle Object", "ESO",
      "Allows you to extend a Speckle object by setting its keys and values.",
      ComponentCategories.PRIMARY_RIBBON, ComponentCategories.OBJECTS)
    {
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      //var pObj = pManager.AddParameter(new SpeckleBaseParam("Speckle Object", "O", "Speckle object to deconstruct into it's properties.", GH_ParamAccess.item));
      pManager.AddGenericParameter("Speckle Object", "O",
        "Speckle object to extend. If the input is not a Speckle Object, it will attempt a conversion of the input first.", GH_ParamAccess.item);
      // All other inputs are dynamically generated by the user.
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddParameter(new SpeckleBaseParam("Extended Speckle Object", "EO", "Extended speckle object.", GH_ParamAccess.item));
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
      if (InPreSolve)
      {
        IGH_Goo inputObj = null;
        DA.GetData(0, ref inputObj);
        Base @base;
        if (inputObj is GH_SpeckleBase speckleBase)
        {
          @base = speckleBase.Value.ShallowCopy();
        }
        else if (inputObj is IGH_Goo goo)
        {
          var value = goo.GetType().GetProperty("Value")?.GetValue(goo);
          if (value is Base baseObj)
          {
            @base = baseObj;
          }
          else if (Converter.CanConvertToSpeckle(value))
          {
            @base = Converter.ConvertToSpeckle(value);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Input object was not a Speckle object, but has been converted to one.");
          }
          else
          {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input object is not a Speckle object, nor can it be converted to one.");
            return;
          }
        }
        else
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input object is not a Speckle object, nor can it be converted to one.");
          return;
        }

        if (Params.Input.Count == 1)
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Please create extra inputs to extend the object with.");
          return;
        }

        var hasErrors = false;
        var allOptional = Params.Input.FindAll(p => p.Optional).Count == Params.Input.Count;
        if (Params.Input.Count > 1 && allOptional)
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "You cannot set all parameters as optional");
          return;
        }

        if (DA.Iteration == 0)
          Tracker.TrackNodeRun("Extend Object");

        var inputData = new Dictionary<string, object>();
        for (int i = 1; i < Params.Input.Count; i++)
        {
          var ighParam = Params.Input[i];
          var param = ighParam as GenericAccessParam;
          var detachable = param.Detachable;
          var key = param.NickName;
          var willOverwrite = false;
          var willChangeDetach = false;
          if (@base.GetMembers().ContainsKey(param.NickName))
          {
            key = param.NickName;
            willOverwrite = true;
            if (detachable)
              willChangeDetach = true;
          }
          else if (@base.GetMembers().ContainsKey("@" + param.NickName))
          {
            key = "@" + param.NickName;
            willOverwrite = true;
            if (!detachable) willChangeDetach = true;
          }
          var targetIndex = DA.ParameterTargetIndex(0);
          var path = DA.ParameterTargetPath(0);

          if (willChangeDetach)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Key {key} already exists in object at {path}[{targetIndex}] with different detach flag. The detach flag of this input will be ignored");
          if (willOverwrite)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
              $"Key {key} already exists in object at {path}[{targetIndex}], its value will be overwritten");

          switch (param.Access)
          {
            case GH_ParamAccess.item:
              object value = null;
              DA.GetData(i, ref value);
              if (!param.Optional && value == null)
              {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                  $"Non-optional parameter {param.NickName} cannot be null");
                hasErrors = true;
              }

              if (value is SpeckleObjectGroup group)
                value = group.Value;
              
              inputData[key] = value;
              break;
            case GH_ParamAccess.list:
              var values = new List<object>();
              DA.GetDataList(i, values);
              if (!param.Optional)
              {
                if (values.Count == 0)
                {
                  AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Non-optional parameter {param.NickName} cannot be null or empty.");
                  hasErrors = true;
                }
              }

              inputData[key] = values;
              break;
            case GH_ParamAccess.tree:
              break;
            default:
              throw new ArgumentOutOfRangeException();
          }
        }

        if (hasErrors) inputData = null;

        var task = Task.Run(() => DoWork(@base, inputData));
        TaskList.Add(task);
        return;
      }

      // Report all conversion errors as warnings
      if (Converter != null)
      {
        foreach (var error in Converter.Report.ConversionErrors)
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
            error.ToFormattedString());
        }
        Converter.Report.ConversionErrors.Clear();
      }

      if (!GetSolveResults(DA, out Base result))
      {
        // Normal mode not supported
        return;
      }

      if (result != null)
      {
        DA.SetData(0, result);
      }
    }

    public bool CanInsertParameter(GH_ParameterSide side, int index) => side == GH_ParameterSide.Input && index != 0;

    public bool CanRemoveParameter(GH_ParameterSide side, int index) => side == GH_ParameterSide.Input && index != 0;

    public IGH_Param CreateParameter(GH_ParameterSide side, int index)
    {
      var myParam = new GenericAccessParam
      {
        Name = GH_ComponentParamServer.InventUniqueNickname("ABCD", Params.Input),
        MutableNickName = true,
        Optional = true
      };

      myParam.NickName = myParam.Name;
      myParam.Optional = false;
      myParam.ObjectChanged += (sender, e) => { };
      myParam.Attributes = new GenericAccessParamAttributes(myParam, Attributes);
      return myParam;
    }

    public bool DestroyParameter(GH_ParameterSide side, int index)
    {
      return true;
    }

    public void VariableParameterMaintenance()
    {
      Params.Input
        .Where(param => !(param.Attributes is GenericAccessParamAttributes))
        .ToList()
        .ForEach(param => param.Attributes = new GenericAccessParamAttributes(param, Attributes)
        );
    }

    public Base DoWork(Base @base, Dictionary<string, object> inputData)
    {
      try
      {
        var hasErrors = false;

        inputData?.Keys.ToList().ForEach(key =>
        {
          var value = inputData[key];
          

          if (value is List<object> list)
          {
            // Value is a list of items, iterate and convert.
            List<object> converted = null;
            try
            {
              converted = list.Select(item =>
              {
                return Converter != null ? Utilities.TryConvertItemToSpeckle(item, Converter) : item;
              }).ToList();
            }
            catch (Exception ex)
            {
              Log.Error(ex, ex.Message);
              AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{ex.ToFormattedString()}");
              hasErrors = true;
            }

            try
            {
              @base[key] = converted;
            }
            catch (Exception ex)
            {
              Log.Error(ex, ex.Message);
              AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{ex.ToFormattedString()}");
              hasErrors = true;
            }
          }
          else
          {
            // If value is not list, it is a single item.

            try
            {
              if (Converter != null)
                @base[key] = value == null ? null : Utilities.TryConvertItemToSpeckle(value, Converter);
              else
                @base[key] = value;
            }
            catch (Exception ex)
            {
              Log.Error(ex, ex.Message);
              AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{ex.ToFormattedString()}");
              hasErrors = true;
            }
          }
        });

        if (hasErrors)
        {
          @base = null;
        }
      }
      catch (Exception ex)
      {
        // If we reach this, something happened that we weren't expecting...
        Log.Error(ex, ex.Message);
        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Something went terribly wrong... " + ex.ToFormattedString());
      }

      return @base;
    }

    private DebounceDispatcher nicknameChangeDebounce = new DebounceDispatcher();

    public override void AddedToDocument(GH_Document document)
    {
      base.AddedToDocument(document); // This would set the converter already.
      Params.ParameterChanged += (sender, args) =>
      {
        if (args.ParameterSide != GH_ParameterSide.Input || args.ParameterIndex == 0) return;
        switch (args.OriginalArguments.Type)
        {
          case GH_ObjectEventType.NickName:
            // This means the user is typing characters, debounce until it stops for 400ms before expiring the solution.
            // Prevents UI from locking too soon while writing new names for inputs.
            args.Parameter.Name = args.Parameter.NickName;
            nicknameChangeDebounce.Debounce(400, (e) => ExpireSolution(true));
            break;
          case GH_ObjectEventType.NickNameAccepted:
            args.Parameter.Name = args.Parameter.NickName;
            ExpireSolution(true);
            break;
        }
      };
    }
  }
}
