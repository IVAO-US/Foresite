namespace ShapefileReader;

using static System.MemoryExtensions;

public static class Reader
{
	public static (double x, double y)[][] ReadShapefile(string filePath)
	{
		using FileStream stream = File.OpenRead(filePath);
		using BinaryReader reader = new(stream);

		List<(double x, double y)[]> polygonParts = [];

		var header = ReadFileHeader(reader);
		for (int bytesRemaining = header.FileLength - 100; bytesRemaining > 0;)
		{
			int recordNumber = Int(reader, true);
			bytesRemaining -= Int(reader, true) * 2 + 8;
			var type = (FileHeader.ShapeType)Int(reader, false);

			switch (type)
			{
				case FileHeader.ShapeType.NullShape:
					break;

				case FileHeader.ShapeType.Point:
					double ptX = Double(reader, false),
						   ptY = Double(reader, false);
					break;

				case FileHeader.ShapeType.MultiPoint:
					FileHeader.BoundingBox mpBox = new(
						Double(reader, false), Double(reader, false),
						Double(reader, false), Double(reader, false),
						0, 0, 0, 0
					);
					for (int remainingPoints = Int(reader, false); remainingPoints > 0; --remainingPoints)
					{
						double x = Double(reader, false),
							   y = Double(reader, false);
					}
					break;

				case FileHeader.ShapeType.Polyline:
					FileHeader.BoundingBox plBox = new(
						Double(reader, false), Double(reader, false),
						Double(reader, false), Double(reader, false),
						0, 0, 0, 0
					);
					int plNumParts = Int(reader, false);
					int plNumPoints = Int(reader, false);
					int[] plPartStarts = new int[plNumParts];
					(double x, double y)[] plPoints = new (double x, double y)[plNumPoints];

					for (int cntr = 0; cntr < plNumParts; ++cntr)
						plPartStarts[cntr] = Int(reader, false);

					for (int cntr = 0; cntr < plNumPoints; ++cntr)
						plPoints[cntr] = (Double(reader, false), Double(reader, false));
					break;

				case FileHeader.ShapeType.Polygon:
					FileHeader.BoundingBox pgBox = new(
						Double(reader, false), Double(reader, false),
						Double(reader, false), Double(reader, false),
						0, 0, 0, 0
					);
					int pgNumParts = Int(reader, false);
					int pgNumPoints = Int(reader, false);
					int[] pgPartStarts = new int[pgNumParts];
					(double x, double y)[] pgPoints = new (double x, double y)[pgNumPoints];

					for (int cntr = 0; cntr < pgNumParts; ++cntr)
						pgPartStarts[cntr] = Int(reader, false);

					for (int cntr = 0; cntr < pgNumPoints; ++cntr)
						pgPoints[cntr] = (Double(reader, false), Double(reader, false));

					for (int cntr = 0; cntr < pgNumParts; ++cntr)
						polygonParts.Add(pgPoints[pgPartStarts[cntr]..(cntr == pgNumParts - 1 ? pgPoints.Length : pgPartStarts[cntr + 1])]);
					break;

				default:
					throw new NotImplementedException();
			}
		}

		return [.. polygonParts];
	}

	private static FileHeader ReadFileHeader(BinaryReader reader)
	{
		if (Int(reader, true) != 9994 ||
			Int(reader, true) != 0 || Int(reader, true) != 0 ||
			Int(reader, true) != 0 || Int(reader, true) != 0 ||
			Int(reader, true) != 0)
			throw new InvalidDataException("File header is incorrect.");

		var fileLength = Int(reader, true) * 2;
		if (Int(reader, false) != 1000)
			throw new InvalidDataException("File header is incorrect.");

		var shapeType = (FileHeader.ShapeType)Int(reader, false);
		FileHeader.BoundingBox boundingBox = new(
			Double(reader, false), Double(reader, false),
			Double(reader, false), Double(reader, false),
			Double(reader, false), Double(reader, false),
			Double(reader, false), Double(reader, false)
		);

		return new(fileLength, shapeType, boundingBox);
	}

	private static int Int(BinaryReader reader, bool bigEndian)
	{
		Span<byte> fourBytes = reader.ReadBytes(4);
		if (fourBytes.Length != 4)
			throw new ArgumentException("Int is 4 bytes", nameof(reader));

		if (BitConverter.IsLittleEndian ^ !bigEndian)
			fourBytes.Reverse();

		return BitConverter.ToInt32(fourBytes);
	}

	private static double Double(BinaryReader reader, bool bigEndian)
	{
		Span<byte> eightBytes = reader.ReadBytes(8);
		if (eightBytes.Length != 8)
			throw new ArgumentException("Double is 8 bytes", nameof(reader));

		if (BitConverter.IsLittleEndian ^ !bigEndian)
			eightBytes.Reverse();

		return BitConverter.ToDouble(eightBytes);
	}
}

public sealed record FileHeader(int FileLength, FileHeader.ShapeType FileShapeType, FileHeader.BoundingBox FileBoundingBox)
{
	public record struct BoundingBox(double Xmin, double Ymin, double Xmax, double Ymax, double Zmin, double Zmax, double Mmin, double Mmax);

	public enum ShapeType : int
	{
		NullShape = 0,
		Point = 1,
		Polyline = 3,
		Polygon = 5,
		MultiPoint = 8,
		PointZ = 11,
		PolylineZ = 13,
		PolygonZ = 15,
		MultiPointZ = 18,
		PointM = 21,
		PolylineM = 23,
		PolygonM = 25,
		MultiPointM = 28,
		MultiPatch = 31
	}
}