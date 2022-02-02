﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Objects.BuiltElements.Archicad;

namespace Archicad.Communication.Commands
{
  sealed internal class CreateWall : ICommand<IEnumerable<string>>
  {

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class Parameters
    {

      [JsonProperty("walls")]
      private IEnumerable<Wall> Datas { get; }

      public Parameters(IEnumerable<Wall> datas)
      {
        Datas = datas;
      }

    }

    [JsonObject(MemberSerialization.OptIn)]
    private sealed class Result
    {

      [JsonProperty("elementIds")]
      public IEnumerable<string> ElementIds { get; private set; }

    }

    private IEnumerable<Wall> Datas { get; }

    public CreateWall(IEnumerable<Wall> datas)
    {
      foreach (var data in datas)
        data.displayValue = null;

      Datas = datas;
    }

    public async Task<IEnumerable<string>> Execute()
    {
      var result = await HttpCommandExecutor.Execute<Parameters, Result>("CreateWall", new Parameters(Datas));
      return result == null ? null : result.ElementIds;
    }

  }
}
