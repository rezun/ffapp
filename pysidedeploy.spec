[app]
title = FFApp
project_dir = .
input_file = main.py
exec_directory = dist
project_file = 
icon = assets/icons/macos/app-icon.icns

[python]
python_path = .venv/bin/python3
packages = Nuitka==2.7.11
android_packages = buildozer==1.5.0,cython==0.29.33

[qt]
qml_files = 
excluded_qml_plugins = 
modules = Core,DBus,Gui,Widgets
plugins = accessiblebridge,egldeviceintegrations,generic,iconengines,imageformats,platforminputcontexts,platforms,platforms/darwin,platformthemes,styles,wayland-decoration-client,wayland-graphics-integration-client,wayland-shell-integration,xcbglintegrations

[android]
wheel_pyside = 
wheel_shiboken = 
plugins = 

[nuitka]
macos.permissions = 
mode = standalone
extra_args = --quiet --noinclude-qt-translations --assume-yes-for-downloads

[buildozer]
mode = debug
recipe_dir = 
jars_dir = 
ndk_path = 
sdk_path = 
local_libs = 
arch = 

