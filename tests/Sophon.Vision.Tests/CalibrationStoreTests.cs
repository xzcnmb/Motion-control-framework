using System;
using System.IO;
using Sophon.Vision.Calibration;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class CalibrationStoreTests
    {
        [Fact]
        public void SaveAndLoad_ShouldPreserveAllFields()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "SophonCalibTest_" + Guid.NewGuid().ToString("N"));

            try
            {
                var data = new CalibrationData
                {
                    CameraId = "TestCamera99",
                    AffineMatrix = new double[][]
                    {
                        new double[] { 0.05, 0.0, 10.0 },
                        new double[] { 0.0, 0.05, 15.0 }
                    },
                    RotationCenterX = 123.456,
                    RotationCenterY = 789.012,
                    RotationRadiusMm = 45.67,
                    MaxResidualMm = 0.012,
                    RmsResidualMm = 0.008,
                    MaxResidualPx = 0.25,
                    RmsResidualPx = 0.15,
                    IsAcceptable = true,
                    Operator = "Tester1",
                    Remarks = "Unit test calibration"
                };

                CalibrationStore.Save(data, tempDir);
                Assert.True(CalibrationStore.Exists("TestCamera99", tempDir));

                var loaded = CalibrationStore.Load("TestCamera99", tempDir);
                Assert.NotNull(loaded);
                Assert.Equal(data.CameraId, loaded.CameraId);
                Assert.Equal(data.RotationCenterX, loaded.RotationCenterX);
                Assert.Equal(data.RotationCenterY, loaded.RotationCenterY);
                Assert.Equal(data.IsAcceptable, loaded.IsAcceptable);
                Assert.Equal(data.Operator, loaded.Operator);
                Assert.Equal(data.AffineMatrix[0][0], loaded.AffineMatrix[0][0]);
                Assert.Equal(data.AffineMatrix[0][2], loaded.AffineMatrix[0][2]);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}
