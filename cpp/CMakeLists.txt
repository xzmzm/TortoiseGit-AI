cmake_minimum_required(VERSION 3.15)
project(TortoiseGitAI)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Make a console application. This will show a console window with debug output.
add_executable(TortoiseGitAI src/main.cpp)

target_include_directories(TortoiseGitAI PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor"
)

# Link against necessary Windows libraries
target_link_libraries(TortoiseGitAI
    user32
    ole32
    oleaut32
    wbemuuid
    winhttp     # for HTTPS requests
)