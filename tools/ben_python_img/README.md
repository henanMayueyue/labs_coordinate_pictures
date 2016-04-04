
### Introduction

Tools for resizing images and converting between formats.

Aims for high quality; uses recent builds of mozjpeg and cwebp rather than PIL's built-in codecs.

### Usage

From another Python script,

	import img_convert_resize
	img_convert_resize.convertOrResizeImage(
		'input.jpg', 'output.jpg', '50%', jpgQuality=95)
	img_convert_resize.convertOrResizeImage(
		'input.png', 'output.webp')

For personal image resizing, rename files with the format "mypicture\_\_MARKAS\_\_50%.jpg", and run

	import img_resize_keep_exif
	img_resize_keep_exif.resizeAllAndKeepExif('/path/to/directory', 
		recurse=False, storeOriginalFilename=True, storeExifFromOriginal=True,
		jpgHighQualityChromaSampling=False)
	img_resize_keep_exif.cleanup('/path/to/directory', recurse=False)

Additional features

* if jpgHighQualityChromaSampling is set to true, jpg files will be larger but will contain better sharpness, especially for red details.
* by default, img_resize_keep_exif will store filename in the Copyright exif tag.
* by default, img_resize_keep_exif will copy the most useful exif data over to the resized image.
* img_utils.readThumbnails to export jpg thumbnails to other jpgs, or remove thumbnail data.
* img_utils.removeResolutionTags to remove jpg resolution tags.
* img_utils.removeAllExifTags to remove all exif tags.
* One sometimes wants the resized jpg to have both dimensions be multiples of 16, as this allows more lossless transformation.
	* Instead of providing a percentage like 50%, provide a dimension like 288h.
	* This means that the smaller dimension (typically height) will be resized to 288 pixels (and aspect ratio preserved).
	* An error will be raised if dimensions could not be made multiples of 16.

### Dependencies

* ben\_python\_common
	* download github.com/downpoured/labs\_coordinate\_music/tree/master/ben\_python\_common
	* place in directory ./tools/ben\_python\_img/ben\_python\_common
* Pillow (a fork of PIL)
	* pip install pillow
* mozjpeg encoder
	* unzip ./tools/mozjpeg\_3.1\_x86.zip
	* (or download from github.com/mozilla/mozjpeg and build)
	* either specify location from coordinate\_pictures UI, (Options->set mozjpeg location)
	* or edit ../options.ini to set FilepathMozJpeg=c:\path\to\cjpeg.exe
* webp encoder
	* download from https://developers.google.com/speed/webp/download
	* either specify location from coordinate\_pictures UI, (Options->set cwebp location)
	* or edit ../options.ini to set FilepathWebp=c:\path\to\cwebp.exe
* exiftool
	* download from http://www.sno.phy.queensu.ca/~phil/exiftool/
	* either specify location from coordinate\_pictures UI, (Options->set exiftool location)
	* or edit ../options.ini to set FilepathExifTool=c:\path\to\exiftool.exe
	