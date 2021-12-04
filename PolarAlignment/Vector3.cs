using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyWeatherData;
using System;

namespace NINA.Plugins.PolarAlignment {
    public class Vector3 {
        public Vector3(double x, double y, double z) {
            X = x;
            Y = y;
            Z = z;
        }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public double Length { 
            get {
                var lenSq = X * X + Y * Y + Z * Z;                    
                return Math.Sqrt(lenSq);
            } 
        }

        public override string ToString() {
            return $"X:{X}; Y:{Y}; Z:{Z}";
        }

        public Vector3 ToUnitVector() {
            if(Length == 0) { return new Vector3(0, 0, 0); }
            return new Vector3(X / Length, Y / Length, Z / Length);
        }

        public TopocentricCoordinates ToTopocentric(Angle latitude, Angle longitude, double elevation) {
            if (X == 0 && Y == 0) { return new TopocentricCoordinates(Angle.ByDegree(0), Angle.ByDegree(90), latitude, longitude, elevation, new SystemDateTime()); }

            var azRad = Y == 0 ? 0 : -Math.Atan2(Y, X);
            var altRad = (Math.PI / 2d) - Math.Acos(Z);

            return new TopocentricCoordinates(Angle.ByRadians(azRad), Angle.ByRadians(altRad), latitude, longitude, elevation, new SystemDateTime());
        }

        /// <summary>
        /// Transform to 3d unit vector
        /// https://mathworld.wolfram.com/SphericalCoordinates.html
        /// </summary>
        /// <param name="coordinates"></param>
        /// <returns></returns>
        public static Vector3 CoordinatesToUnitVector(Coordinates coordinates, Angle latitude, Angle longitude, double elevation, WeatherDataInfo weatherDataInfo) {
            TopocentricCoordinates topo;
            if (weatherDataInfo?.Connected == true) {
                double pressurehPa = weatherDataInfo.Pressure;
                double temperature = weatherDataInfo.Temperature;
                double relativeHumidity = weatherDataInfo.Humidity;
                const double wavelength = 0.55d;
                Logger.Info($"Transforming coordinates with refraction parameters. Pressure={pressurehPa}, Temperature={temperature}, Humidity={relativeHumidity}, Wavelength={wavelength}");
                topo = coordinates.Transform(latitude, longitude, elevation, pressurehPa, temperature, relativeHumidity, wavelength);
            } else {
                topo = coordinates.Transform(latitude, longitude, elevation);
            }

            return CoordinatesToUnitVector(topo);
        }


        /// <summary>
        /// Transform to 3d unit vector
        /// https://mathworld.wolfram.com/SphericalCoordinates.html
        /// </summary>
        /// <param name="coordinates"></param>
        /// <returns></returns>
        public static Vector3 CoordinatesToUnitVector(TopocentricCoordinates topo) {
            var theta = -topo.Azimuth.Radians;
            var phi = (Math.PI / 2d) - topo.Altitude.Radians;
            var x = Math.Cos(theta) * Math.Sin(phi);
            var y = Math.Sin(theta) * Math.Sin(phi);
            var z = Math.Cos(phi);

            return new Vector3(x, y, z);
        }

        /// <summary>
        /// Determines the plane out of three unit vectors
        /// </summary>
        /// <param name="A"></param>
        /// <param name="B"></param>
        /// <param name="C"></param>
        /// <returns></returns>
        public static Vector3 DeterminePlaneVector(Vector3 A, Vector3 B, Vector3 C) {
            // Determine the plane that goes through the three points A, B, C and its defining Vector
            // (B - A) x (C - A)
            var v2Minusv1 = B - A;
            var v3Minusv2 = C - B;

            //Cross product of v2Minusv1 x v3Minusv2
            var cross = CrossProduct(v2Minusv1, v3Minusv2);

            //Convert to Unit Vector
            return cross.ToUnitVector();
        }

        public static Vector3 operator -(Vector3 B, Vector3 A) {
            return new Vector3(B.X - A.X, B.Y - A.Y, B.Z - A.Z);
        }

        public static Vector3 operator +(Vector3 B, Vector3 A) {
            return new Vector3(B.X + A.X, B.Y + A.Y, B.Z + A.Z);
        }

        public static Vector3 operator *(Vector3 B, Vector3 A) {
            return new Vector3(B.X * A.X, B.Y * A.Y, B.Z * A.Z);
        }

        public static Vector3 operator *(Vector3 A, double rad) {
            return new Vector3(A.X * rad, A.Y * rad, A.Z * rad);
        }

        public static Vector3 CrossProduct(Vector3 v1, Vector3 v2) {
            return new Vector3(v1.Y * v2.Z - v1.Z * v2.Y,
                            v1.Z * v2.X - v1.X * v2.Z,
                            v1.X * v2.Y - v1.Y * v2.X);
        }

        public static double ScalarProduct(Vector3 v1, Vector3 v2) {
            return v1.X * v2.X + v1.Y * v2.Y + v1.Z * v2.Z;
        }

        public static Vector3 Project(Vector3 a, Vector3 b) {
            return b * (ScalarProduct(a, b) / ScalarProduct(b, b));
        }

        /// <summary>
        /// https://en.wikipedia.org/wiki/Rodrigues%27_rotation_formula
        /// </summary>
        /// <param name="v">a vector in R³</param>
        /// <param name="k">a unit vector describing an axis of rotation about which v rotates</param>
        /// <param name="degrees">the angle that v should rotate by</param>
        /// <returns></returns>
        public static Vector3 RotateByRodrigues(Vector3 v, Vector3 k, Angle degrees) {
            return v * Math.Cos(degrees.Radians) + Vector3.CrossProduct(k, v) * Math.Sin(degrees.Radians) + k * Vector3.ScalarProduct(k, v) * (1 - Math.Cos(degrees.Radians));
        }
    }
}
