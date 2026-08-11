// file: nonunderscore_target_specs.c
#include "nonunderscore_target_specs_private.h"

/* Private file declarations. */
int32_t nativeCount(void);

int32_t useSpecs(uint8_t *data, uintptr_t count, void (* callback)(void), int32_t (* typedCallback)(void))
{
	return (nativeCount() + (int32_t)(count));
}

// file: nonunderscore_target_specs.h
#ifndef NONUNDERSCORE_TARGET_SPECS_H_
#define NONUNDERSCORE_TARGET_SPECS_H_

#include "nonunderscore_target_specs_private.h"

int32_t useSpecs(uint8_t *data, uintptr_t count, void (* callback)(void), int32_t (* typedCallback)(void));

#endif
// file: nonunderscore_target_specs_private.h
#ifndef NONUNDERSCORE_TARGET_SPECS_PRIVATE_H_
#define NONUNDERSCORE_TARGET_SPECS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
int32_t useSpecs(uint8_t *data, uintptr_t count, void (* callback)(void), int32_t (* typedCallback)(void));

/* Object declarations. */


#endif
