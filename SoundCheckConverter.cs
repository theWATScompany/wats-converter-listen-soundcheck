using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using Virinco.WATS.Interface;

namespace Virinco.WATS.Converter.Listen
{
    public class SoundCheckConverter : IReportConverter_v2
    {
        private readonly Dictionary<string, string> parameters;

        private CultureInfo cultureInfo;
        private int _colOffset; // 0 = old format (no Operator column), 1 = new format (with Operator column)

        public SoundCheckConverter() : base()
        {
            parameters = new Dictionary<string, string>()
            {
                { "partRevision", "1.0" },
                { "operationTypeCode", "10" },
                { "operator", "oper" },
                { "sequenceName", "SoundCheckSeq" },
                { "sequenceVersion", "1.0.0" },
                { "cultureCode", "en-US" }
            };
        }

        public SoundCheckConverter(IDictionary<string, string> parameters) : this()
        {
            foreach (var parameter in parameters)
                this.parameters[parameter.Key] = parameter.Value;
        }

        public Dictionary<string, string> ConverterParameters => parameters;

        public void CleanUp()
        {
        }

        public Report ImportReport(TDM api, Stream file)
        {
            cultureInfo = new CultureInfo(parameters["cultureCode"]);

            UUTReport unitTestReport = null;
            using (TextReader reader = new StreamReader(file))
            {
                //Serial #:	Time: 	Lot #: 	Name	Margin	Unit	Result	Tolerance	Limits
                // New format adds "Operator: " as second column — detect by header
                string header = reader.ReadLine();
                string[] headerCols = header?.Split('\t') ?? Array.Empty<string>();
                _colOffset = (headerCols.Length > 1 && headerCols[1].Trim().StartsWith("Operator", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                string line = reader.ReadLine();
                string sn = "", measureName = "", xUnit = "", yUnit = "";
                double[] xValues = null;
                while (line != null)
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        string[] columns = line.Split(new char[] { '\t' });
                        if (columns[0] != "Serial #:")
                            sn = columns[0];
                        if (unitTestReport == null || unitTestReport.SerialNumber != sn)
                        {
                            if (unitTestReport != null)
                                api.Submit(unitTestReport);
                            unitTestReport = CreateUUTFromHeader(api, columns, sn);
                        }
                        if (columns[0] == "Serial #:")
                            ReadGraphHeader(unitTestReport, columns, out measureName, out xUnit, out yUnit);
                        else if (columns.Length > 10 + _colOffset)
                            ReadGraphData(unitTestReport, columns, measureName, xUnit, yUnit, ref xValues);
                        else
                            ReadMeasure(unitTestReport, columns);
                    }
                    line = reader.ReadLine();
                }
                if (unitTestReport != null)
                    api.Submit(unitTestReport);
            }
            return unitTestReport;
        }

        private void ReadGraphData(UUTReport unitTestReport, string[] columns, string measureName, string xUnit, string yUnit, ref double[] xValues)
        {
            List<double> values = new List<double>();
            for (int i = 3 + _colOffset; i < columns.Length; i++)
            {
                if (!string.IsNullOrEmpty(columns[i]))
                    values.Add(double.Parse(columns[i], cultureInfo));
            }

            if (xValues == null)
            {
                xValues = values.ToArray();
                return;
            }
            NumericLimitStep numericLimitStep = unitTestReport.GetRootSequenceCall().AddNumericLimitStep(measureName);
            Chart chart = numericLimitStep.AddChart(ChartType.LineLogY, measureName, "X", xUnit, "Y", yUnit);
            chart.AddSeries(measureName, xValues, values.ToArray());
            xValues = null;
        }

        private void ReadGraphHeader(UUTReport unitTestReport, string[] columns, out string measureName, out string xUnit, out string yUnit)
        {
            measureName = columns[3 + _colOffset];
            xUnit = columns[13 + _colOffset];
            yUnit = columns[14 + _colOffset];
            ;
        }

        private void ReadMeasure(UUTReport unitTestReport, string[] columns)
        {
            NumericLimitStep numericLimitStep = unitTestReport.GetRootSequenceCall().AddNumericLimitStep(columns[3 + _colOffset]);
            double measure = double.Parse(columns[4 + _colOffset], cultureInfo);
            if (columns[8 + _colOffset].Contains("/"))
            {
                string[] lim = columns[8 + _colOffset].Split(new char[] { '/' });
                double lowLim = double.Parse(lim[1], cultureInfo);
                double hiLim = double.Parse(lim[0], cultureInfo);
                numericLimitStep.AddTest(measure, CompOperatorType.GELE, lowLim, hiLim, columns[5 + _colOffset]);
            }
            else
                numericLimitStep.AddTest(measure, columns[5 + _colOffset]);
            numericLimitStep.Status = StepStatus(columns[6 + _colOffset]);
        }

        private UUTReport CreateUUTFromHeader(TDM api, string[] cols, string sn)
        {
            UUTReport uut = api.CreateUUTReport(parameters["operator"],
                cols[2 + _colOffset], parameters["partRevision"],
                sn, parameters["operationTypeCode"],
                parameters["sequenceName"], parameters["sequenceVersion"]);
            string timeStr = 1 + _colOffset < cols.Length ? cols[1 + _colOffset].Trim() : string.Empty;
            if (!DateTime.TryParse(timeStr, cultureInfo, System.Globalization.DateTimeStyles.None, out DateTime dt) &&
                !DateTime.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt))
                dt = DateTime.UtcNow;
            uut.StartDateTime = dt;
            return uut;
        }

        StepStatusType StepStatus(string input)
        {
            return
                input.ToLower() == "passed" ||
                input.ToLower() == "pass" ? StepStatusType.Passed :
                input.ToLower() == "failed" ||
                input.ToLower() == "fail" ? StepStatusType.Failed :
                input.ToLower() == "terminated" ? StepStatusType.Terminated :
                input.ToLower() == "error" ? StepStatusType.Error :
                input.ToLower() == "done" ? StepStatusType.Done :
                input.ToLower() == "skipped" ? StepStatusType.Skipped : StepStatusType.Error;
        }
    }
}
