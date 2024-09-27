import math
import numpy as np
from astropy.time import Time
from astropy import units as u
from astropy.coordinates import AltAz
from astropy.coordinates import SkyCoord
from astropy.coordinates import EarthLocation
from astropy.coordinates import ICRS
from astropy.coordinates import FK5
from astropy.coordinates import Angle
from astropy.coordinates import CartesianRepresentation
from scipy.spatial.transform import Rotation

# location on Earth of the observer
latitude = Angle(40.0 * u.degree)
longitude = Angle(0.0 * u.degree)
# azimuth and altitude components of the mount polar axis misalignment
e_azm = Angle(1.0 * u.degree)
e_alt = Angle(1.0 * u.degree)
# astropy coordinate systems are left-handed, so a positive angle
# means a counterclockwise rotation, from the point of view of an observer
# at the origin looking along the axis or rotation
theta = Angle(30 * u.degree)
# azimuth and altitude of the first measured point
al0 = Angle(20.0 * u.degree)
az0 = Angle(70.0 * u.degree)
# time of observation
time = Time('2000-01-01 00:00:00', scale='utc')
# atmosphere parameters
temperature = 7 * u.Celsius
pressure = 1005 * u.hPa
humidity = 0.8
# observation wavelength
obswl = 0.574 * u.micron

loc = EarthLocation.from_geodetic(lat=latitude, lon=longitude, height=0.0*u.meter)
# mount's polar axis
pa = AltAz(obstime=time,
           location=loc,
           alt=latitude + e_alt,
           az=e_azm,
           pressure=pressure,
           temperature=temperature,
           relative_humidity=humidity,
           obswl=obswl)
pa_v = pa.cartesian
# quaternion representing a rotation around this axis
q = [math.sin(theta.radian / 2)*pa_v.x, 
                          math.sin(theta.radian / 2)*pa_v.y, 
                          math.sin(theta.radian / 2)*pa_v.z,
                          math.cos(theta.radian / 2)]
# rotation matrix
rot = Rotation.from_quat(q/np.linalg.norm(q))

# first measurement point
p1 = AltAz(obstime=time,
           location=loc,
           alt=al0,
           az=az0,
           pressure=pressure,
           temperature=temperature,
           relative_humidity=humidity,
           obswl=obswl)
# rotate first measurement point around mount's polar axis to obtain second
p1_v = p1.cartesian.xyz
p2_v = rot.apply(p1_v)
p2 = AltAz(CartesianRepresentation(p2_v), 
           obstime=time,
           location=loc,
           pressure=pressure,
           temperature=temperature,
           relative_humidity=humidity,
           obswl=obswl)
# rotate second measurement point to obtain third
p3_v = rot.apply(p2_v)
p3 = AltAz(CartesianRepresentation(p3_v), 
           obstime=time,
           location=loc,
           pressure=pressure,
           temperature=temperature,
           relative_humidity=humidity,
           obswl=obswl)

# determine the equatorial coordinates in the ICRS reference system
# of the three measurement points. It will take refraction into account
# if the parameters of the atmosphere are provided
rs = ICRS()
s1 = p1.transform_to(rs)
s2 = p2.transform_to(rs)
s3 = p3.transform_to(rs)
print(s1)
print(s2)
print(s3)
