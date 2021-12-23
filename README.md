# Heron
Heron is a Grasshopper add-on which enables the import and export of GIS data into the Rhino 3d/Grasshopper environment, located, scaled and cropped based on Rhino's EarthAnchorPoint and a clipping polygon.  Heron is built on GDAL libraries and can import many GIS vector, raster and topographic file types, export vector GIS data and consume GIS REST services over the web.

The add-on includes components in five categories.

![toolbar-v0_3_9](https://user-images.githubusercontent.com/13613796/147161114-da93d1f6-5e9b-4884-b33e-4ace94beb404.PNG)
# 
### GIS Import | Export
Components for importing and exporting GIS data.  
- **Import Vector**: Import vector GIS data clipped to a boundary, including SHP, GeoJSON, OSM, KML, MVT, GDB folders and HTTP sources.
- **Import Topo**: Create a topographic mesh from a raster file (IMG, HGT, ASCII, DEM, TIF, etc) clipped to a boundary.
- **Import Raster**: Import georeferenced raster data.
- **Import OSM**: Import vector OpenStreetMap data clipped to a boundary. Nodes, Ways and Relations are organized onto their own branches in the output.  Building massing will also be included if it exists in the OSM data.
- **Import LAZ**: Import LAS & LAZ files. Built on laszip.net.
- **Export Vector**: Export Grasshopper geometry to Shapefile, GeoJSON, KML and GML file formats in the WGS84 (EPSG:4326) spatial reference system.
# 
### GIS Tools
Components for translating between Rhino and GIS coordinates and processing GIS data with GDAL programs.
- **Set EarthAnchorPoint**: Set the Rhino EarthAnchorPoint.  Setting the EAP is necessary for most Heron components to work properly.
- **XY to Decimal Degrees**: Convert XY to Decimal Degrees Longitude/Latitude in the WGS84 spatial reference system.
- **Decimal Degrees to XY**: Convert WGS84 Decimal Degrees Longitude/Latitude to X/Y.
- **Coordinate Transformation**: Transform points from a source SRS to a destination SRS. The source points should be in the coordinate system of the source SRS.
- **Gdal Ogr2Ogr**: Manipulate vector data with the GDAL OGR2OGR program given a source dataset, a destination dataset and a list of options. Information about conversion options can be found at https://gdal.org/programs/ogr2ogr.html.
- **Gdal Warp**: Manipulate raster data with the GDAL Warp program given a source dataset, a destination dataset and a list of options. Information about Warp options can be found at https://gdal.org/programs/gdalwarp.html.
- **Gdal Translate**: Manipulate raster data with the GDAL Translate program given a source dataset, a destination dataset and a list of options.  Information about Translate options can be found at https://gdal.org/programs/gdal_translate.html.
# 
### GIS REST
Components for interacting with REST web services.
- **ESRI REST Service Geocode**: Get coordinates based on a Point-of-Interest or Address using the ESRI geocode service.
- **ESRI REST Service Reverse Geocode**: Get the closest addresses to XY coordinates using the ESRI reverse geocode service.
- **Get REST Service Layers**: Discover ArcGIS REST Service Layers.
- **Get REST Vector**: Get vector data from ArcGIS REST Services.
- **Get REST Topo**: Get STRM, ALOS and GMRT topographic data from web services.  These services include global coverage from the Shuttle Radar Topography Mission (SRTM GL3 90m and SRTM GL1 30m), Advanced Land Observing Satellite (ALOS World 3D - 30m) and Global Multi-Resolution Topography (GMRT including bathymetry). Sources are opentopography.org and gmrt.org.
- **Get REST Raster**: Get raster imagery from ArcGIS REST Services.
- **Get REST OSM**: Get an OSM vector file within a boundary from web services such as the Overpass API.  Use a search term to filter results and increase speed. 
#
### GIS API
Components for interacting with tile-based services and services requiring a token.
- **Slippy Viewport**: Projects the boundary of a given Viewport to the World XY plane and calculates a good Zoom level for use with tile-based map components.
- **Slippy Tiles**: Visualize boundaries of slippy map tiles within a given boundary at a given zoom level.  See https://en.wikipedia.org/wiki/Tiled_web_map for more information about map tiles.
- **Slippy Raster**: Get raster imagery from a tile-based map service. Use the component menu to select the service.
- **Mapbox Vector**: Get vector data from a Mapbox service. Requires a Mapbox Token.
- **Mapbox Raster**: Get raster imagery from a Mapbox service. Requires a Mapbox Token.
- **Mapbox Topo**: Get mesh topography from a Mapbox service. Requires a Mapbox Token.
#
### Utilities
Non-GIS components 
- **Cubemap from View**: Generate a cubemap from a given plane using the specified display mode.
- **Cubemap from View Plus**: Generate a cubemap from a given plane using the specified display mode.  This component is also able to visualize ray casting based on colors in the cubemap.
- **Cubemap to Equirectangular**: Convert a cube map panorama to an equirectangular panorama.
- **Image Filtered Colors**: Get a filtered pixel count of colors contained in an image based on color list.
- **Image Top Colors**: Get a sorted list of the top colors contained in an image.
- **Flip Image**: Flip an image along its vertical, horizontal axis or both.
- **Rotate Image**: Roate an image 90, 180 or 270 degrees.
- **Multi Mesh Patch**: Multithreaded creation of mesh patches from planar polylines. The first polyine in a branch will be considered the outer boundary, any others will be considered holes and should be completely within the outer boundary.
- **Multi SDiff**: This multithreaded boolean solid difference (SDiff) component spreads the branches of input over threads for the boolean operation. 
- **Multi Move to Topo**: Move breps, surfaces, meshes, polylines and points to a topography mesh.  Breps and closed meshes will be moved to the lowest point on the topography mesh within their footprint. Vertexes of curves and open meshes and control points of surfaces will be moved to the topography mesh. Geometry on a branch will be moved together as a group, but can be moved independently by deselecting 'Group' from the component menu. For a slower, but more detailed projection where curves and open meshes take on the vertexes of the topography mesh, select 'Detailed' from the component menu.
- **Color to Hex**: Convert an RGBA color to hexidecimal format.
- **Hex to Color**: Convert a hexidecimal color to RGBA format.
- **Visual Center**: Find the visual center of closed planar curves. The resulting point will lie within the boundary of the curve and multiple curves on a branch will be treated as a surface with holes.
