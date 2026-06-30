// file: conversion_callable_abi_slot_compatible.c
#include "conversion_callable_abi_slot_compatible_private.h"


void abiCompatible(int32_t (* acceptsLarge)(uint8_t *_large), int32_t (* acceptsSmall)(uint8_t *_small))
{
	int32_t (* targetSmall)(uint8_t *_small) = acceptsLarge;
	int32_t (* targetLarge)(uint8_t *_large) = acceptsSmall;
}

// file: conversion_callable_abi_slot_compatible.h
#ifndef CONVERSION_CALLABLE_ABI_SLOT_COMPATIBLE_H_
#define CONVERSION_CALLABLE_ABI_SLOT_COMPATIBLE_H_

#include "conversion_callable_abi_slot_compatible_private.h"

void abiCompatible(int32_t (* acceptsLarge)(uint8_t *_large), int32_t (* acceptsSmall)(uint8_t *_small));

#endif
// file: conversion_callable_abi_slot_compatible_private.h
#ifndef CONVERSION_CALLABLE_ABI_SLOT_COMPATIBLE_PRIVATE_H_
#define CONVERSION_CALLABLE_ABI_SLOT_COMPATIBLE_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */
void abiCompatible(int32_t (* acceptsLarge)(uint8_t *_large), int32_t (* acceptsSmall)(uint8_t *_small));

/* Object declarations. */


#endif
