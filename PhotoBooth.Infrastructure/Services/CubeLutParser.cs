using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PhotoBooth.Core.Models;
using PhotoBooth.Core.Services;

namespace PhotoBooth.Infrastructure.Services
{
    internal sealed class CubeLutParser : IColorLutParser
    {
        const long MaximumFileBytes=128L*1024*1024;
        const int MaximumLineLength=4096;
        public ColorLutValidationResult Validate(string filePath,System.Threading.CancellationToken token)
        {
            try{using(var data=Parse(filePath,token))return new ColorLutValidationResult{IsValid=true,Metadata=data.Metadata,Warnings=data.Metadata.CubeSize>65?new[]{"LUT larger than 65³ is capture-only and will not be used for live view."}:new string[0]};}
            catch(Exception e) when(e is InvalidDataException||e is IOException||e is UnauthorizedAccessException){return new ColorLutValidationResult{IsValid=false,Errors=new[]{e.Message}};}
        }
        public ColorLutData Parse(string filePath,System.Threading.CancellationToken token)
        {
            var info=new FileInfo(filePath);if(!info.Exists)throw new FileNotFoundException("LUT file was not found.",filePath);if(info.Length<=0||info.Length>MaximumFileBytes)throw new InvalidDataException("LUT file size must be between 1 byte and 128 MiB.");
            int size=0,lineNumber=0;string title=null;var min=new[]{0f,0f,0f};var max=new[]{1f,1f,1f};bool hasMin=false,hasMax=false,samplesStarted=false;var values=new List<float>();
            using(var stream=new FileStream(filePath,FileMode.Open,FileAccess.Read,FileShare.Read))using(var reader=new StreamReader(stream,true))
            {
                string raw;while((raw=reader.ReadLine())!=null)
                {
                    token.ThrowIfCancellationRequested();lineNumber++;if(raw.Length>MaximumLineLength)throw Error(lineNumber,"Line exceeds 4096 characters.");
                    var comment=raw.IndexOf('#');var line=(comment>=0?raw.Substring(0,comment):raw).Trim();if(line.Length==0)continue;
                    var parts=Split(line);var directive=parts[0].ToUpperInvariant();
                    if(directive=="TITLE"){if(samplesStarted)throw Error(lineNumber,"TITLE is not allowed after samples.");title=ParseTitle(line,lineNumber);continue;}
                    if(directive=="LUT_1D_SIZE")throw Error(lineNumber,"Only 3D .cube LUTs are supported.");
                    if(directive=="LUT_3D_SIZE")
                    {
                        if(samplesStarted||size!=0||parts.Length!=2||!int.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out size))throw Error(lineNumber,"Invalid or duplicate LUT_3D_SIZE.");
                        if(size<2||size>128)throw Error(lineNumber,"LUT_3D_SIZE must be between 2 and 128.");continue;
                    }
                    if(directive=="DOMAIN_MIN"||directive=="DOMAIN_MAX")
                    {
                        if(samplesStarted)throw Error(lineNumber,directive+" is not allowed after samples.");var target=directive=="DOMAIN_MIN"?min:max;ParseTriple(parts,target,lineNumber);
                        if(directive=="DOMAIN_MIN"){if(hasMin)throw Error(lineNumber,"Duplicate DOMAIN_MIN.");hasMin=true;}else{if(hasMax)throw Error(lineNumber,"Duplicate DOMAIN_MAX.");hasMax=true;}continue;
                    }
                    if(size==0)throw Error(lineNumber,"LUT_3D_SIZE must appear before sample data.");
                    if(parts.Length!=3)throw Error(lineNumber,"Each 3D LUT sample must contain exactly three values.");samplesStarted=true;
                    for(var i=0;i<3;i++){float value;if(!float.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out value)||float.IsNaN(value)||float.IsInfinity(value))throw Error(lineNumber,"Sample contains an invalid number.");values.Add(value);}
                    var expected=(long)size*size*size*3;if(values.Count>expected)throw Error(lineNumber,"LUT contains more samples than LUT_3D_SIZE declares.");
                }
            }
            if(size==0)throw new InvalidDataException("LUT_3D_SIZE is required.");if(min[0]>=max[0]||min[1]>=max[1]||min[2]>=max[2])throw new InvalidDataException("DOMAIN_MIN must be lower than DOMAIN_MAX for every channel.");
            var required=(long)size*size*size*3;if(values.Count!=required)throw new InvalidDataException("LUT sample count does not match LUT_3D_SIZE; expected "+(required/3).ToString(CultureInfo.InvariantCulture)+" RGB samples.");
            return new ColorLutData{Metadata=new ColorLutMetadata{Title=title,CubeSize=size,DomainMinR=min[0],DomainMinG=min[1],DomainMinB=min[2],DomainMaxR=max[0],DomainMaxG=max[1],DomainMaxB=max[2]},Values=values.ToArray()};
        }
        static string[] Split(string line)=>line.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
        static string ParseTitle(string line,int number){var value=line.Substring(5).Trim();if(value.Length>=2&&value[0]=='"'&&value[value.Length-1]=='"')return value.Substring(1,value.Length-2);if(value.Length==0)throw Error(number,"TITLE is empty.");return value;}
        static void ParseTriple(string[] parts,float[] target,int number){if(parts.Length!=4)throw Error(number,"Domain directive requires three values.");for(var i=0;i<3;i++)if(!float.TryParse(parts[i+1],NumberStyles.Float,CultureInfo.InvariantCulture,out target[i])||float.IsNaN(target[i])||float.IsInfinity(target[i]))throw Error(number,"Domain contains an invalid number.");}
        static InvalidDataException Error(int line,string message)=>new InvalidDataException("Line "+line.ToString(CultureInfo.InvariantCulture)+": "+message);
    }
}
