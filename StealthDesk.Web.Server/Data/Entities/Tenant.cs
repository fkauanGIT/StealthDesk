using System.ComponentModel.DataAnnotations;
using StealthDesk.Web.Server.Data.Entities.Bases;

namespace StealthDesk.Web.Server.Data.Entities;

public class Tenant : EntityBase
{
  public List<Device>? Devices { get; set; }

  [StringLength(100)]
  public string? Name { get; set; }
}
