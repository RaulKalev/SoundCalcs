using System.Collections.Generic;
using Newtonsoft.Json;
using SoundCalcs.Domain;
using SoundCalcs.IO;
using Xunit;

namespace SoundCalcs.Tests
{
    public class SettingsStoreTests
    {
        [Fact]
        public void WallTypes_SurviveTheRoundTripAsCatalogEntries()
        {
            // Regression: the wall type came back as the default (200 mm concrete) for every group
            var s = new PluginSettings
            {
                WallGroups = new List<WallLineGroup>
                {
                    new WallLineGroup { WallType = WallTypeCatalog.All[2] },
                    new WallLineGroup { WallType = WallTypeCatalog.All[WallTypeCatalog.All.Count - 1] }
                }
            };
            string json = JsonConvert.SerializeObject(s, SettingsStore.JsonSettings);
            var back = JsonConvert.DeserializeObject<PluginSettings>(json, SettingsStore.JsonSettings);
            Assert.Same(WallTypeCatalog.All[2], back.WallGroups[0].WallType);
            Assert.Same(WallTypeCatalog.All[WallTypeCatalog.All.Count - 1], back.WallGroups[1].WallType);
        }

        [Fact]
        public void UnknownWallTypeKey_TakesTheNearestRating()
        {
            string json = "{\"WallGroups\":[{\"WallType\":{\"Key\":\"gone\",\"StcRating\":55}}]}";
            var back = JsonConvert.DeserializeObject<PluginSettings>(json, SettingsStore.JsonSettings);
            Assert.Equal(WallTypeCatalog.FindClosestByStc(55), back.WallGroups[0].WallType);
        }
    }
}
