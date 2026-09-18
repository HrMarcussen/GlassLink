idf_component_register(
    SRCS "main.c" "usb_link.c" "display.c" "ident.c"
    INCLUDE_DIRS "."
    REQUIRES driver esp_lcd nvs_flash app_update esp_timer esp_driver_jpeg esp_driver_ppa esp_mm hal soc
)
target_compile_definitions(${COMPONENT_LIB} PRIVATE FW_VERSION="${PROJECT_VER}" FW_BUILD="${GL_BUILD}")
