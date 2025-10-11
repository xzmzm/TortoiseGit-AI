cmake_minimum_required(VERSION 3.15)
project(TortoiseGitAI)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Make a Windows GUI application (no console window)
add_executable(TortoiseGitAI WIN32 src/main.cpp)

# For development, you might want a console window.
# add_executable(TortoiseGitAI src/main.cpp)

target_include_directories(TortoiseGitAI PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor"
)

# Use Windows Secure Channel (SChannel) for HTTPS and disable ZLib support.
# To disable ZLIB support in httplib.h, the CPPHTTPLIB_ZLIB_SUPPORT macro must not be defined.
# The previous line was defining it to 0, which still caused httplib.h to look for zlib.h.
target_compile_definitions(TortoiseGitAI PRIVATE CPPHTTPLIB_WIN_SCHANNEL_SUPPORT)

# Link against necessary Windows libraries
target_link_libraries(TortoiseGitAI
    user32
    ole32
    oleaut32
    wbemuuid
    ws2_32    # for networking
    crypt32   # for SChannel
    secur32   # for SChannel
)