using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Content-aware crop. Greedily shrinks the bounding rect by removing the
/// row or column with lowest Shannon entropy at each step until target dims
/// are reached. The result is the rectangle inside the input that retains
/// the most "interesting" content. Common in image services for thumbnail
/// generation that focuses on faces / text / detail rather than centering
/// blindly. Mirrors libvips' <c>vips_smartcrop</c> with attention=ENTROPY.
/// </summary>
