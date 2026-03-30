<template>
	<div id="app">
		
	</div>
</template>

<script>
	import { createNamespacedHelpers } from "vuex";
	const { mapGetters, mapActions } = createNamespacedHelpers("module");


	export default {
		name: "app",
		data: function () {
			return {
				exampleData: 0
			}
		},
		props: {
			// type check
			exampleProp1: Number,
			// type check plus other validations
			exampleProp2: {
				type: Number,
				default: 0,
				required: false,
				validator: function (value) {
					return value >= 0
				}
			}
		},
		computed: {
			exampleComputed: function () {
				return 0;
			},
			exampleGetSet: {
				get: function () {
					return 0;
				},
				set: function () {
				}
			},
			...mapGetters([
				
			])
		},
		methods: {
			exampleMethod: function () {
				return 0;
			},
			...mapActions([
				
			])
		},
		watch: {
			exampleData: function () {

			}
		},
		filters: {
			capitalize: function (value) {
				if (!value) return ""
				value = value.toString()
				return value.charAt(0).toUpperCase() + value.slice(1)
			}//{{ message | capitalize }}
		},
		components: {
		},
		beforeCreate: function () {
			
			
		},
		created: function () {


		},
		beforeMount: function () {


		},
		mounted: function () {


		},
		beforeUpdate: function () {


		},
		updated: function () {


		},
		activated: function () {


		},
		deactivated: function () {


		},
		beforeDestroy: function () {


		},
		
		destroyed: function () {
			
		},
		errorCaptured: function () {


		}
	};
</script>

<style scoped>
	
</style>
